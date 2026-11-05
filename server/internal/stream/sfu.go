package stream

import (
	"fmt"
	"io"
	"log"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/interceptor"
	"github.com/pion/rtcp"
	"github.com/pion/webrtc/v4"
)

// ICEServerConfig is one STUN/TURN entry exposed to clients and used by Pion.
type ICEServerConfig struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

// SFU is an in-process Pion selective-forwarding unit: one publisher track per
// employee room, fan-out to N admin subscribers. No JPEG path.
type SFU struct {
	iceServers     []webrtc.ICEServer
	api            *webrtc.API
	maxBitrateKbps int
	onPublisherPLI func(empID string)

	mu    sync.Mutex
	rooms map[string]*sfuRoom
}

type sfuRoom struct {
	mu sync.Mutex

	publisherPC *webrtc.PeerConnection
	track       *webrtc.TrackLocalStaticRTP
	trackReady  chan struct{}
	trackOnce   sync.Once
	pliDone     chan struct{}

	subscribers map[uint64]*webrtc.PeerConnection
	pubActive   atomic.Bool
	// subOfferGen bumps on every AcceptSubscriberOffer so a superseded
	// in-flight offer can bail out instead of erroring the new PC.
	subOfferGen map[uint64]uint64

	// trackReadyListeners are notified once when a publisher track becomes available
	// AFTER an answer that had no media (late renegotiation).
	trackReadyListeners map[uint64]chan struct{}
}

// NewSFU builds a Pion API with the given ICE servers (empty → default Google STUN).
func NewSFU(ice []ICEServerConfig, maxBitrateKbps int) *SFU {
	servers := toPionICE(ice)
	m := &webrtc.MediaEngine{}
	if err := m.RegisterDefaultCodecs(); err != nil {
		log.Printf("[webrtc-sfu] RegisterDefaultCodecs: %v", err)
	}
	i := &interceptor.Registry{}
	if err := webrtc.RegisterDefaultInterceptors(m, i); err != nil {
		log.Printf("[webrtc-sfu] RegisterDefaultInterceptors: %v", err)
	}
	api := webrtc.NewAPI(
		webrtc.WithMediaEngine(m),
		webrtc.WithInterceptorRegistry(i),
	)
	if maxBitrateKbps <= 0 {
		maxBitrateKbps = 8000
	}
	return &SFU{
		iceServers:     servers,
		api:            api,
		maxBitrateKbps: maxBitrateKbps,
		rooms:          make(map[string]*sfuRoom),
	}
}

// SetPublisherPLIHook registers a callback invoked when the SFU sends a PLI to the publisher.
func (s *SFU) SetPublisherPLIHook(fn func(empID string)) {
	s.onPublisherPLI = fn
}

func toPionICE(ice []ICEServerConfig) []webrtc.ICEServer {
	if len(ice) == 0 {
		return []webrtc.ICEServer{{URLs: []string{"stun:stun.l.google.com:19302"}}}
	}
	out := make([]webrtc.ICEServer, 0, len(ice))
	for _, s := range ice {
		if len(s.URLs) == 0 {
			continue
		}
		out = append(out, webrtc.ICEServer{
			URLs:       s.URLs,
			Username:   s.Username,
			Credential: s.Credential,
		})
	}
	if len(out) == 0 {
		return []webrtc.ICEServer{{URLs: []string{"stun:stun.l.google.com:19302"}}}
	}
	return out
}

// ICEServersJSON returns ICE config for watch-ticket / client hello responses.
func (s *SFU) ICEServersJSON() []ICEServerConfig {
	out := make([]ICEServerConfig, 0, len(s.iceServers))
	for _, srv := range s.iceServers {
		cfg := ICEServerConfig{
			URLs:     append([]string(nil), srv.URLs...),
			Username: srv.Username,
		}
		if c, ok := srv.Credential.(string); ok {
			cfg.Credential = c
		}
		out = append(out, cfg)
	}
	return out
}

func (s *SFU) getOrCreateRoom(empID string) *sfuRoom {
	s.mu.Lock()
	defer s.mu.Unlock()
	r, ok := s.rooms[empID]
	if ok {
		return r
	}
	r = &sfuRoom{
		subscribers:         make(map[uint64]*webrtc.PeerConnection),
		trackReady:          make(chan struct{}),
		trackReadyListeners: make(map[uint64]chan struct{}),
		subOfferGen:         make(map[uint64]uint64),
	}
	s.rooms[empID] = r
	return r
}

// PublisherActive reports whether the employee peer has an active media track.
func (s *SFU) PublisherActive(empID string) bool {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return false
	}
	return r.pubActive.Load()
}

func (r *sfuRoom) closePublisherLocked() {
	r.pubActive.Store(false)
	if r.pliDone != nil {
		select {
		case <-r.pliDone:
		default:
			close(r.pliDone)
		}
		r.pliDone = nil
	}
	if r.publisherPC != nil {
		_ = r.publisherPC.Close()
		r.publisherPC = nil
	}
	r.track = nil
	// Unblock anyone waiting on the previous generation, then start a fresh gate.
	r.trackOnce.Do(func() { close(r.trackReady) })
	r.trackReady = make(chan struct{})
	r.trackOnce = sync.Once{}
}

func (r *sfuRoom) notifyTrackReadyLocked() {
	for id, ch := range r.trackReadyListeners {
		select {
		case ch <- struct{}{}:
		default:
		}
		delete(r.trackReadyListeners, id)
	}
}

// ClosePublisher tears down the employee PeerConnection.
func (s *SFU) ClosePublisher(empID string) {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.closePublisherLocked()
}

// CloseRoom removes all peers for an employee.
func (s *SFU) CloseRoom(empID string) {
	s.mu.Lock()
	r := s.rooms[empID]
	delete(s.rooms, empID)
	s.mu.Unlock()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.closePublisherLocked()
	for id, pc := range r.subscribers {
		_ = pc.Close()
		delete(r.subscribers, id)
	}
	for id, ch := range r.trackReadyListeners {
		close(ch)
		delete(r.trackReadyListeners, id)
	}
}

// WatchTrackReady returns a one-shot channel that fires when a publisher track
// becomes available. If a track is already present (race: arrived after a
// withTrack=false answer), the channel is signaled immediately so the watcher
// can renegotiate — leaving it idle permanently blanked LIVE tiles.
func (s *SFU) WatchTrackReady(empID string, watcherID uint64) <-chan struct{} {
	r := s.getOrCreateRoom(empID)
	ch := make(chan struct{}, 1)
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.track != nil {
		ch <- struct{}{}
		return ch
	}
	if r.trackReadyListeners == nil {
		r.trackReadyListeners = make(map[uint64]chan struct{})
	}
	r.trackReadyListeners[watcherID] = ch
	return ch
}

// UnwatchTrackReady removes a track-ready listener.
func (s *SFU) UnwatchTrackReady(empID string, watcherID uint64) {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	delete(r.trackReadyListeners, watcherID)
}

// AcceptPublisherOffer creates the publisher PC and returns the answer SDP.
func (s *SFU) AcceptPublisherOffer(empID, sdp string, onICE func(candidate webrtc.ICECandidateInit)) (answerSDP string, err error) {
	r := s.getOrCreateRoom(empID)
	r.mu.Lock()
	defer r.mu.Unlock()

	r.closePublisherLocked()

	pc, err := s.api.NewPeerConnection(webrtc.Configuration{ICEServers: s.iceServers})
	if err != nil {
		return "", err
	}
	r.publisherPC = pc

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil || onICE == nil {
			return
		}
		onICE(c.ToJSON())
	})

	pc.OnTrack(func(remote *webrtc.TrackRemote, receiver *webrtc.RTPReceiver) {
		local, err := webrtc.NewTrackLocalStaticRTP(remote.Codec().RTPCodecCapability, "video", "alpha-live")
		if err != nil {
			log.Printf("[webrtc-sfu] NewTrackLocalStaticRTP employee=%s: %v", empID, err)
			return
		}
		r.mu.Lock()
		r.track = local
		r.pubActive.Store(true)
		r.trackOnce.Do(func() { close(r.trackReady) })
		r.notifyTrackReadyLocked()
		if r.pliDone != nil {
			select {
			case <-r.pliDone:
			default:
				close(r.pliDone)
			}
		}
		r.pliDone = make(chan struct{})
		pliDone := r.pliDone
		r.mu.Unlock()

		log.Printf("[webrtc-sfu] publisher track ready employee=%s codec=%s", empID, remote.Codec().MimeType)
		go forwardRTP(remote, local, &r.pubActive, s.maxBitrateKbps)
		// One IDR nudge for late joiners — NOT every PLI tick (that flooded ctrl
		// and could drop start/stop on the buffered channel).
		if s.onPublisherPLI != nil {
			s.onPublisherPLI(empID)
		}
		go sendPLI(pc, remote, pliDone)
		_ = receiver
	})

	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		if state == webrtc.PeerConnectionStateFailed ||
			state == webrtc.PeerConnectionStateClosed {
			r.pubActive.Store(false)
		}
	})

	if err := pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer,
		SDP:  sdp,
	}); err != nil {
		r.closePublisherLocked()
		return "", err
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		r.closePublisherLocked()
		return "", err
	}
	if err := pc.SetLocalDescription(answer); err != nil {
		r.closePublisherLocked()
		return "", err
	}
	log.Printf("[webrtc-sfu] publisher answer employee=%s sdpBytes=%d", empID, len(answer.SDP))
	return answer.SDP, nil
}

func forwardRTP(remote *webrtc.TrackRemote, local *webrtc.TrackLocalStaticRTP, active *atomic.Bool, maxBitrateKbps int) {
	buf := make([]byte, 1500)
	// Token-bucket egress cap (Pion v4 has no RTPSender.SetMaxBitrate).
	bytesPerSec := float64(maxBitrateKbps) * 1000.0 / 8.0
	if bytesPerSec < 1 {
		bytesPerSec = 8000 * 1000.0 / 8.0
	}
	tokens := bytesPerSec
	last := time.Now()
	for {
		n, _, readErr := remote.Read(buf)
		if readErr != nil {
			active.Store(false)
			return
		}
		if n > len(buf) {
			log.Printf("[webrtc-sfu] dropping oversized RTP packet n=%d", n)
			continue
		}
		now := time.Now()
		tokens += now.Sub(last).Seconds() * bytesPerSec
		if tokens > bytesPerSec*2 {
			tokens = bytesPerSec * 2
		}
		last = now
		if float64(n) > tokens {
			continue // over WEBRTC_MAX_BITRATE_KBPS — drop
		}
		tokens -= float64(n)
		if _, writeErr := local.Write(buf[:n]); writeErr != nil && writeErr != io.ErrClosedPipe {
			active.Store(false)
			return
		}
	}
}

// sendPLI periodically requests a decoder refresh from the publisher via RTCP.
// Encoder ForceKeyFrame is triggered separately (one-shot on track ready) —
// wiring every PLI tick to the ctrl channel caused I-frame spam + dropped start/stop.
func sendPLI(pc *webrtc.PeerConnection, remote *webrtc.TrackRemote, done <-chan struct{}) {
	ticker := time.NewTicker(2 * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-done:
			return
		case <-ticker.C:
			if err := pc.WriteRTCP([]rtcp.Packet{
				&rtcp.PictureLossIndication{MediaSSRC: uint32(remote.SSRC())},
			}); err != nil {
				return
			}
		}
	}
}

// AddPublisherICE adds a remote ICE candidate to the publisher PC.
func (s *SFU) AddPublisherICE(empID string, cand webrtc.ICECandidateInit) error {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return fmt.Errorf("no room")
	}
	r.mu.Lock()
	pc := r.publisherPC
	r.mu.Unlock()
	if pc == nil {
		return fmt.Errorf("no publisher pc")
	}
	return pc.AddICECandidate(cand)
}

const subscriberTrackWait = 12 * time.Second

// AcceptSubscriberOffer creates a subscriber PC and returns the answer SDP.
// PC is created immediately (so trickle ICE is accepted), then we briefly wait
// for the publisher track so the answer can carry media — one negotiation.
//
// Returns withTrack=true when the answer includes the publisher video track.
// A superseded in-flight offer (replaced by a newer one) returns err=errSuperseded
// without tearing down the newer PC.
func (s *SFU) AcceptSubscriberOffer(empID string, watcherID uint64, sdp string, onICE func(candidate webrtc.ICECandidateInit)) (answerSDP string, withTrack bool, err error) {
	r := s.getOrCreateRoom(empID)

	pc, err := s.api.NewPeerConnection(webrtc.Configuration{ICEServers: s.iceServers})
	if err != nil {
		return "", false, err
	}

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil || onICE == nil {
			return
		}
		onICE(c.ToJSON())
	})
	// Only remove THIS pc — closing a replaced peer must not delete the successor.
	pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		if state == webrtc.PeerConnectionStateFailed || state == webrtc.PeerConnectionStateClosed {
			s.RemoveSubscriberIf(empID, watcherID, pc)
		}
	})

	r.mu.Lock()
	if r.subOfferGen == nil {
		r.subOfferGen = make(map[uint64]uint64)
	}
	r.subOfferGen[watcherID]++
	myGen := r.subOfferGen[watcherID]
	if old, ok := r.subscribers[watcherID]; ok {
		_ = old.Close()
	}
	r.subscribers[watcherID] = pc
	track := r.track
	readyCh := r.trackReady
	r.mu.Unlock()

	if err := pc.SetRemoteDescription(webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer,
		SDP:  sdp,
	}); err != nil {
		s.RemoveSubscriberIf(empID, watcherID, pc)
		return "", false, err
	}

	if track == nil {
		timer := time.NewTimer(subscriberTrackWait)
		select {
		case <-readyCh:
			timer.Stop()
		case <-timer.C:
		}
		r.mu.Lock()
		track = r.track
		r.mu.Unlock()
	}

	// Bail if a newer offer replaced us while we waited.
	r.mu.Lock()
	if r.subOfferGen[watcherID] != myGen {
		r.mu.Unlock()
		_ = pc.Close()
		return "", false, errSuperseded
	}
	r.mu.Unlock()

	if track != nil {
		if _, err := pc.AddTrack(track); err != nil {
			s.RemoveSubscriberIf(empID, watcherID, pc)
			return "", false, err
		}
		withTrack = true
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		s.RemoveSubscriberIf(empID, watcherID, pc)
		return "", false, err
	}
	if err := pc.SetLocalDescription(answer); err != nil {
		s.RemoveSubscriberIf(empID, watcherID, pc)
		return "", false, err
	}

	r.mu.Lock()
	stillMine := r.subOfferGen[watcherID] == myGen && r.subscribers[watcherID] == pc
	r.mu.Unlock()
	if !stillMine {
		_ = pc.Close()
		return "", false, errSuperseded
	}

	log.Printf("[webrtc-sfu] subscriber answer employee=%s watcher=%d withTrack=%v", empID, watcherID, withTrack)
	return answer.SDP, withTrack, nil
}

var errSuperseded = fmt.Errorf("superseded")

// ErrSuperseded is returned when a newer subscriber offer replaced an in-flight one.
var ErrSuperseded = errSuperseded

// AddSubscriberICE adds a remote ICE candidate for a watcher.
func (s *SFU) AddSubscriberICE(empID string, watcherID uint64, cand webrtc.ICECandidateInit) error {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return fmt.Errorf("no room")
	}
	r.mu.Lock()
	pc := r.subscribers[watcherID]
	r.mu.Unlock()
	if pc == nil {
		return fmt.Errorf("no subscriber pc")
	}
	return pc.AddICECandidate(cand)
}

// RemoveSubscriber closes one admin PeerConnection.
func (s *SFU) RemoveSubscriber(empID string, watcherID uint64) {
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if pc, ok := r.subscribers[watcherID]; ok {
		_ = pc.Close()
		delete(r.subscribers, watcherID)
	}
	delete(r.trackReadyListeners, watcherID)
}

// RemoveSubscriberIf closes the subscriber only when it is still the mapped PC.
func (s *SFU) RemoveSubscriberIf(empID string, watcherID uint64, pc *webrtc.PeerConnection) {
	if pc == nil {
		return
	}
	s.mu.Lock()
	r := s.rooms[empID]
	s.mu.Unlock()
	if r == nil {
		return
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if cur, ok := r.subscribers[watcherID]; ok && cur == pc {
		_ = pc.Close()
		delete(r.subscribers, watcherID)
	}
}

// Close shuts down every room.
func (s *SFU) Close() {
	s.mu.Lock()
	ids := make([]string, 0, len(s.rooms))
	for id := range s.rooms {
		ids = append(ids, id)
	}
	s.mu.Unlock()
	for _, id := range ids {
		s.CloseRoom(id)
	}
}
