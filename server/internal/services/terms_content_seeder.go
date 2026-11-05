package services

import (
	"context"
	"log"

	"github.com/alpha-ai-tracker/server/internal/repository"
)

// featuredTerm describes one system-seeded T&C entry.
type featuredTerm struct {
	FeatureID    string
	Heading      string
	Body         string
	TermsVersion string
	SortOrder    int
}

// FeaturedTermsSeed is the ordered list of built-in terms seeded on first boot.
// Each maps to a client-side feature ID. The seeder checks existence by feature_id
// before inserting, so re-running is idempotent.
var FeaturedTermsSeed = []featuredTerm{
	{
		FeatureID:    "app_usage",
		Heading:      "Application Usage Tracking",
		TermsVersion: "1.0",
		SortOrder:    1,
		Body: `<h3>What this means for you</h3><p>The tracker records which applications you open and close on this computer, how long each one stays open, and whether it is in the foreground or background.</p>` +
			`<h3>What is collected</h3><p>Application name, when you opened and closed it, how long it was used, and whether it was the active window.</p>` +
			`<h3>Why it is collected</h3><p>Your organisation uses this information to measure productivity, track working time, and generate attendance and activity reports.</p>` +
			`<h3>Who can see it</h3><p>Only authorised administrators in your organisation. The data is not sold or shared outside your company.</p>` +
			`<h3>Your rights</h3><p>This feature is required. You must accept these terms to continue using the tracker. You cannot revoke consent for Application Usage Tracking while the tracker is active on your machine.</p>`,
	},
	{
		FeatureID:    "browser_journey",
		Heading:      "Browser Journey Tracking",
		TermsVersion: "1.0",
		SortOrder:    2,
		Body: `<h3>What this means for you</h3><p>The tracker records the websites and web pages you visit in browsers (Chrome, Firefox, Edge, and others) and in embedded browsers inside other apps (for example VS Code, Slack, or Teams).</p>` +
			`<h3>What is collected</h3><p>Page title, website address (URL), domain name, browser name, whether the window was private or incognito, and when the page was opened and closed.</p>` +
			`<h3>Why it is collected</h3><p>Your organisation uses this information to understand which online resources are used for work and to support compliance and productivity reviews.</p>` +
			`<h3>Who can see it</h3><p>Only authorised administrators in your organisation.</p>` +
			`<h3>Your rights</h3><p>This feature is optional. You may decline now or revoke your consent later. Turning it off does not affect Application Usage Tracking or any other features you have accepted. Previously collected data is retained until it expires under your organisation’s retention policy.</p>`,
	},
	{
		FeatureID:    "file_journey",
		Heading:      "File Explorer Journey Tracking",
		TermsVersion: "1.0",
		SortOrder:    3,
		Body: `<h3>What this means for you</h3><p>The tracker records activity in the file manager (File Explorer, Nautilus, Finder, and similar): folders you open and files you create, rename, delete, or open.</p>` +
			`<h3>What is collected</h3><p>Folder and file paths, the action performed (open, create, rename, delete, navigate), and when the action happened.</p>` +
			`<h3>What is not collected</h3><p>The content of your files is never opened or read. Only the path and the type of action are recorded. System, cache, and application-data folders are excluded.</p>` +
			`<h3>Why it is collected</h3><p>Your organisation uses this information for security reviews and to understand how work documents and project folders are used.</p>` +
			`<h3>Your rights</h3><p>This feature is optional. You may decline now or revoke your consent later. Previously collected data is retained until it expires under your organisation’s retention policy.</p>`,
	},
	{
		FeatureID:    "live_view",
		Heading:      "Live Screen Viewing",
		TermsVersion: "1.0",
		SortOrder:    4,
		Body: `<h3>What this means for you</h3><p>If you accept, authorised administrators may start a live viewing session of your screen when needed (for example for remote support or a specific compliance check).</p>` +
			`<h3>How it works</h3><p>Live viewing does not run continuously in the background. A session only starts when an authorised administrator explicitly begins it. Sessions are temporary and are logged.</p>` +
			`<h3>Who can start a session</h3><p>Only administrators who have the required permissions in your organisation.</p>` +
			`<h3>Your rights</h3><p>This feature is optional and is off by default. You may decline now or revoke your consent later. After you revoke consent, no new live viewing sessions can be started on your device until you accept again.</p>`,
	},
}

// TermsContentSeeder ensures featured (system) T&C entries exist in the database.
// It runs once at server startup after migrations. For each entry in FeaturedTermsSeed,
// it checks if a row with that feature_id already exists; if not, it inserts one.
// Featured terms are: is_system=true, term_type='featured_based', is_active=1.
type TermsContentSeeder struct {
	repo *repository.TermsContentRepo
}

func NewTermsContentSeeder(repo *repository.TermsContentRepo) *TermsContentSeeder {
	return &TermsContentSeeder{repo: repo}
}

// SeedFeaturedTerms is idempotent — safe to call on every startup.
func (s *TermsContentSeeder) SeedFeaturedTerms(ctx context.Context) error {
	for _, t := range FeaturedTermsSeed {
		exists, err := s.repo.ExistsByFeatureID(ctx, t.FeatureID)
		if err != nil {
			log.Printf("[seeder] WARN: failed to check feature_id=%s: %v", t.FeatureID, err)
			continue
		}
		if exists {
			log.Printf("[seeder] featured term already exists: feature_id=%s — skipping", t.FeatureID)
			continue
		}

		_, err = s.repo.InsertFeaturedTerm(ctx, &repository.TermsContent{
			Heading:      t.Heading,
			Body:         t.Body,
			TermsVersion: t.TermsVersion,
			IsSystem:     true,
			FeatureID:    t.FeatureID,
			TermType:     "featured_based",
			IsActive:     1,
			SortOrder:    t.SortOrder,
		})
		if err != nil {
			log.Printf("[seeder] ERROR: failed to insert featured term feature_id=%s: %v", t.FeatureID, err)
			continue
		}
		log.Printf("[seeder] seeded featured term: feature_id=%s heading=%q", t.FeatureID, t.Heading)
	}
	return nil
}
