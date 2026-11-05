package services

import "math"

// HaversineM returns the great-circle distance in metres between two WGS84 points.
func HaversineM(lat1, lon1, lat2, lon2 float64) float64 {
	const earthRadius = 6371000.0
	rad := math.Pi / 180.0
	dLat := (lat2 - lat1) * rad
	dLon := (lon2 - lon1) * rad
	a := math.Sin(dLat/2)*math.Sin(dLat/2) +
		math.Cos(lat1*rad)*math.Cos(lat2*rad)*math.Sin(dLon/2)*math.Sin(dLon/2)
	c := 2 * math.Atan2(math.Sqrt(a), math.Sqrt(1-a))
	return earthRadius * c
}

func IsInsideGeofence(sampleLat, sampleLon, zoneLat, zoneLon, radiusM float64) bool {
	return HaversineM(sampleLat, sampleLon, zoneLat, zoneLon) <= radiusM
}
