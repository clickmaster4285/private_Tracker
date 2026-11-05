package repository

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/alpha-ai-tracker/server/internal/models"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"
)

type DlpRepo struct {
	pool *pgxpool.Pool
}

func NewDlpRepo(pool *pgxpool.Pool) *DlpRepo {
	return &DlpRepo{pool: pool}
}

// ── Rules ───────────────────────────────────────────────────────────────────

func (r *DlpRepo) ListRules(ctx context.Context) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT r.id::text, r.name, r.enabled, r.trigger, r.action, r.severity, r.scope_all,
		       COALESCE((SELECT array_agg(e.extension ORDER BY e.extension)
		                 FROM dlp_rule_extensions e WHERE e.rule_id=r.id), ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.pattern ORDER BY p.pattern)
		                 FROM dlp_rule_patterns p WHERE p.rule_id=r.id), ARRAY[]::text[]),
		       r.created_at, r.updated_at
		FROM dlp_rules r WHERE r.deleted_at IS NULL
		ORDER BY r.created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanDlpRules(rows)
}

func (r *DlpRepo) ListEnabledRules(ctx context.Context) ([]models.DlpRule, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT r.id::text, r.name, r.enabled, r.trigger, r.action, r.severity, r.scope_all,
		       COALESCE((SELECT array_agg(e.extension ORDER BY e.extension)
		                 FROM dlp_rule_extensions e WHERE e.rule_id=r.id), ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.pattern ORDER BY p.pattern)
		                 FROM dlp_rule_patterns p WHERE p.rule_id=r.id), ARRAY[]::text[]),
		       r.created_at, r.updated_at
		FROM dlp_rules r WHERE r.deleted_at IS NULL AND r.enabled=TRUE
		ORDER BY r.created_at ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanDlpRules(rows)
}

func (r *DlpRepo) GetRule(ctx context.Context, id string) (*models.DlpRule, error) {
	rule, err := getDlpRule(ctx, r.pool, id)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	return rule, err
}

func (r *DlpRepo) CreateRule(ctx context.Context, name, trigger, action, severity string, enabled, scopeAll bool, extensions, patterns []string) (*models.DlpRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)
	var id string
	err = tx.QueryRow(ctx, `
		INSERT INTO dlp_rules (name,enabled,trigger,action,severity,scope_all)
		VALUES ($1,$2,$3,$4,$5,$6) RETURNING id::text`,
		name, enabled, trigger, action, severity, scopeAll).Scan(&id)
	if err != nil {
		return nil, err
	}
	if err := replaceRuleValues(ctx, tx, id, extensions, patterns); err != nil {
		return nil, err
	}
	rule, err := getDlpRule(ctx, tx, id)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return rule, nil
}

func (r *DlpRepo) UpdateRule(ctx context.Context, id string, name *string, enabled *bool, trigger, action, severity *string, scopeAll *bool, extensions, patterns *[]string, changedBy *string) (*models.DlpRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)
	cur, err := getDlpRule(ctx, tx, id)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	if err := insertDlpRuleRevision(ctx, tx, *cur, changedBy); err != nil {
		return nil, err
	}
	if name != nil {
		cur.Name = *name
	}
	if enabled != nil {
		cur.Enabled = *enabled
	}
	if trigger != nil {
		cur.Trigger = *trigger
	}
	if action != nil {
		cur.Action = *action
	}
	if severity != nil {
		cur.Severity = *severity
	}
	if scopeAll != nil {
		cur.ScopeAll = *scopeAll
	}
	if _, err := tx.Exec(ctx, `
		UPDATE dlp_rules SET name=$2,enabled=$3,trigger=$4,action=$5,severity=$6,scope_all=$7,updated_at=NOW()
		WHERE id=$1::uuid AND deleted_at IS NULL`,
		id, cur.Name, cur.Enabled, cur.Trigger, cur.Action, cur.Severity, cur.ScopeAll); err != nil {
		return nil, err
	}
	nextExtensions, nextPatterns := cur.Extensions, cur.Patterns
	if extensions != nil {
		nextExtensions = *extensions
	}
	if patterns != nil {
		nextPatterns = *patterns
	}
	if extensions != nil || patterns != nil {
		if err := replaceRuleValues(ctx, tx, id, nextExtensions, nextPatterns); err != nil {
			return nil, err
		}
	}
	rule, err := getDlpRule(ctx, tx, id)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return rule, nil
}

func (r *DlpRepo) ListDlpRuleRevisions(ctx context.Context, ruleID string) ([]models.DlpRuleRevision, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT rev.id, rev.rule_id::text, rev.revision, rev.name, rev.enabled, rev.trigger, rev.action, rev.severity,
		       rev.scope_all,
		       COALESCE((SELECT array_agg(e.extension ORDER BY e.extension)
		                 FROM dlp_revision_extensions e WHERE e.revision_id=rev.id), ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.pattern ORDER BY p.pattern)
		                 FROM dlp_revision_patterns p WHERE p.revision_id=rev.id), ARRAY[]::text[]),
		       rev.changed_by::text, COALESCE(u.name, ''), rev.created_at
		FROM dlp_rule_revisions rev
		LEFT JOIN users u ON u.id = rev.changed_by AND u.deleted_at IS NULL
		WHERE rev.rule_id = $1::uuid
		ORDER BY rev.revision DESC`, ruleID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]models.DlpRuleRevision, 0)
	for rows.Next() {
		var rev models.DlpRuleRevision
		var changedBy *string
		if err := rows.Scan(
			&rev.ID, &rev.RuleID, &rev.Revision, &rev.Name, &rev.Enabled, &rev.Trigger, &rev.Action, &rev.Severity,
			&rev.ScopeAll, &rev.Extensions, &rev.Patterns,
			&changedBy, &rev.ChangedByName, &rev.CreatedAt,
		); err != nil {
			return nil, err
		}
		rev.ChangedBy = changedBy
		if rev.Extensions == nil {
			rev.Extensions = []string{}
		}
		if rev.Patterns == nil {
			rev.Patterns = []string{}
		}
		out = append(out, rev)
	}
	return out, rows.Err()
}

func insertDlpRuleRevision(ctx context.Context, tx pgx.Tx, cur models.DlpRule, changedBy *string) error {
	var nextRev int
	if err := tx.QueryRow(ctx,
		`SELECT COALESCE(MAX(revision),0)+1 FROM dlp_rule_revisions WHERE rule_id=$1::uuid`,
		cur.ID).Scan(&nextRev); err != nil {
		return err
	}

	var revisionID int64
	if changedBy != nil && *changedBy != "" {
		if err := tx.QueryRow(ctx, `
			INSERT INTO dlp_rule_revisions
				(rule_id,revision,name,enabled,trigger,action,severity,scope_all,changed_by)
			VALUES ($1::uuid,$2,$3,$4,$5,$6,$7,$8,$9::uuid)
			RETURNING id`,
			cur.ID, nextRev, cur.Name, cur.Enabled, cur.Trigger, cur.Action, cur.Severity, cur.ScopeAll, *changedBy,
		).Scan(&revisionID); err != nil {
			return err
		}
	} else {
		if err := tx.QueryRow(ctx, `
			INSERT INTO dlp_rule_revisions
				(rule_id,revision,name,enabled,trigger,action,severity,scope_all)
			VALUES ($1::uuid,$2,$3,$4,$5,$6,$7,$8)
			RETURNING id`,
			cur.ID, nextRev, cur.Name, cur.Enabled, cur.Trigger, cur.Action, cur.Severity, cur.ScopeAll,
		).Scan(&revisionID); err != nil {
			return err
		}
	}
	return replaceDlpRevisionValues(ctx, tx, revisionID, cur.Extensions, cur.Patterns)
}

func replaceDlpRevisionValues(ctx context.Context, tx pgx.Tx, revisionID int64, extensions, patterns []string) error {
	for _, ext := range extensions {
		ext = strings.TrimSpace(ext)
		if ext == "" {
			continue
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO dlp_revision_extensions (revision_id, extension) VALUES ($1,$2)
			 ON CONFLICT (revision_id, extension) DO NOTHING`, revisionID, ext); err != nil {
			return err
		}
	}
	for _, pat := range patterns {
		pat = strings.TrimSpace(pat)
		if pat == "" {
			continue
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO dlp_revision_patterns (revision_id, pattern) VALUES ($1,$2)
			 ON CONFLICT (revision_id, pattern) DO NOTHING`, revisionID, pat); err != nil {
			return err
		}
	}
	return nil
}

func (r *DlpRepo) SoftDeleteRule(ctx context.Context, id string) error {
	tag, err := r.pool.Exec(ctx, `
		UPDATE dlp_rules SET deleted_at = NOW() WHERE id = $1::uuid AND deleted_at IS NULL`, id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("dlp rule not found")
	}
	return nil
}

// ── Web blocker rules ───────────────────────────────────────────────────────

const webBlockerSelect = `
	SELECT r.id::text,r.name,r.enabled,r.action,r.severity,r.scope_all,
	       COALESCE((SELECT array_agg(d.domain ORDER BY d.domain) FROM web_blocker_domains d WHERE d.rule_id=r.id),ARRAY[]::text[]),
	       COALESCE((SELECT array_agg(p.path_pattern ORDER BY p.path_pattern) FROM web_blocker_paths p WHERE p.rule_id=r.id),ARRAY[]::text[]),
	       r.schedule_enabled,r.schedule_start,r.schedule_end,r.schedule_timezone,
	       COALESCE((SELECT array_agg(s.day ORDER BY s.day) FROM web_blocker_schedule_days s WHERE s.rule_id=r.id),ARRAY[]::text[]),
	       r.created_at,r.updated_at
	FROM web_blocker_rules r`

func (r *DlpRepo) ListWebBlockerRules(ctx context.Context) ([]models.WebBlockerRule, error) {
	rows, err := r.pool.Query(ctx, webBlockerSelect+` WHERE r.deleted_at IS NULL ORDER BY r.created_at DESC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanWebBlockerRules(rows)
}

func (r *DlpRepo) ListEnabledWebBlockerRules(ctx context.Context) ([]models.WebBlockerRule, error) {
	rows, err := r.pool.Query(ctx, webBlockerSelect+` WHERE r.deleted_at IS NULL AND r.enabled=TRUE ORDER BY r.created_at ASC`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	return scanWebBlockerRules(rows)
}

func (r *DlpRepo) GetWebBlockerRule(ctx context.Context, id string) (*models.WebBlockerRule, error) {
	rule, err := getWebBlockerRule(ctx, r.pool, id)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	return rule, err
}

func (r *DlpRepo) CreateWebBlockerRule(ctx context.Context, rule models.WebBlockerRule) (*models.WebBlockerRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)
	err = tx.QueryRow(ctx, `
		INSERT INTO web_blocker_rules
			(name,enabled,action,severity,scope_all,schedule_enabled,schedule_start,schedule_end,schedule_timezone)
		VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9) RETURNING id::text`,
		rule.Name, rule.Enabled, rule.Action, rule.Severity, rule.ScopeAll,
		rule.ScheduleEnabled, rule.ScheduleStart, rule.ScheduleEnd, rule.ScheduleTimezone).Scan(&rule.ID)
	if err != nil {
		return nil, err
	}
	if err := replaceWebBlockerValues(ctx, tx, rule.ID, rule.Domains, rule.Paths, rule.ScheduleDays); err != nil {
		return nil, err
	}
	created, err := getWebBlockerRule(ctx, tx, rule.ID)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return created, nil
}

func (r *DlpRepo) UpdateWebBlockerRule(ctx context.Context, id string, rule models.WebBlockerRule, changedBy *string) (*models.WebBlockerRule, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	cur, err := getWebBlockerRule(ctx, tx, id)
	if err != nil {
		return nil, err
	}
	if cur == nil {
		return nil, nil
	}
	if err := insertWebBlockerRevision(ctx, tx, *cur, changedBy); err != nil {
		return nil, err
	}

	tag, err := tx.Exec(ctx, `
		UPDATE web_blocker_rules SET name=$2,enabled=$3,action=$4,severity=$5,scope_all=$6,
			schedule_enabled=$7,schedule_start=$8,schedule_end=$9,schedule_timezone=$10, updated_at=NOW()
		WHERE id=$1::uuid AND deleted_at IS NULL`,
		id, rule.Name, rule.Enabled, rule.Action, rule.Severity, rule.ScopeAll,
		rule.ScheduleEnabled, rule.ScheduleStart, rule.ScheduleEnd, rule.ScheduleTimezone)
	if err != nil {
		return nil, err
	}
	if tag.RowsAffected() == 0 {
		return nil, nil
	}
	if err := replaceWebBlockerValues(ctx, tx, id, rule.Domains, rule.Paths, rule.ScheduleDays); err != nil {
		return nil, err
	}
	updated, err := getWebBlockerRule(ctx, tx, id)
	if err != nil {
		return nil, err
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return updated, nil
}

func (r *DlpRepo) ListWebBlockerRevisions(ctx context.Context, ruleID string) ([]models.WebBlockerRuleRevision, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT rev.id, rev.rule_id::text, rev.revision, rev.name, rev.enabled, rev.action, rev.severity,
		       rev.scope_all,
		       COALESCE((SELECT array_agg(d.domain ORDER BY d.domain)
		                 FROM web_blocker_revision_domains d WHERE d.revision_id=rev.id), ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.path_pattern ORDER BY p.path_pattern)
		                 FROM web_blocker_revision_paths p WHERE p.revision_id=rev.id), ARRAY[]::text[]),
		       rev.schedule_enabled, rev.schedule_start, rev.schedule_end, rev.schedule_timezone,
		       COALESCE((SELECT array_agg(s.day ORDER BY s.day)
		                 FROM web_blocker_revision_schedule_days s WHERE s.revision_id=rev.id), ARRAY[]::text[]),
		       rev.changed_by::text, COALESCE(u.name, ''), rev.created_at
		FROM web_blocker_rule_revisions rev
		LEFT JOIN users u ON u.id = rev.changed_by AND u.deleted_at IS NULL
		WHERE rev.rule_id = $1::uuid
		ORDER BY rev.revision DESC`, ruleID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := make([]models.WebBlockerRuleRevision, 0)
	for rows.Next() {
		var rev models.WebBlockerRuleRevision
		var changedBy *string
		if err := rows.Scan(
			&rev.ID, &rev.RuleID, &rev.Revision, &rev.Name, &rev.Enabled, &rev.Action, &rev.Severity,
			&rev.ScopeAll, &rev.Domains, &rev.Paths, &rev.ScheduleEnabled, &rev.ScheduleStart,
			&rev.ScheduleEnd, &rev.ScheduleTimezone, &rev.ScheduleDays,
			&changedBy, &rev.ChangedByName, &rev.CreatedAt,
		); err != nil {
			return nil, err
		}
		rev.ChangedBy = changedBy
		if rev.Domains == nil {
			rev.Domains = []string{}
		}
		if rev.Paths == nil {
			rev.Paths = []string{}
		}
		if rev.ScheduleDays == nil {
			rev.ScheduleDays = []string{}
		}
		out = append(out, rev)
	}
	return out, rows.Err()
}

func insertWebBlockerRevision(ctx context.Context, tx pgx.Tx, cur models.WebBlockerRule, changedBy *string) error {
	var nextRev int
	if err := tx.QueryRow(ctx,
		`SELECT COALESCE(MAX(revision),0)+1 FROM web_blocker_rule_revisions WHERE rule_id=$1::uuid`,
		cur.ID).Scan(&nextRev); err != nil {
		return err
	}

	var revisionID int64
	if changedBy != nil && *changedBy != "" {
		if err := tx.QueryRow(ctx, `
			INSERT INTO web_blocker_rule_revisions
				(rule_id,revision,name,enabled,action,severity,scope_all,
				 schedule_enabled,schedule_start,schedule_end,schedule_timezone,changed_by)
			VALUES ($1::uuid,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12::uuid)
			RETURNING id`,
			cur.ID, nextRev, cur.Name, cur.Enabled, cur.Action, cur.Severity, cur.ScopeAll,
			cur.ScheduleEnabled, cur.ScheduleStart, cur.ScheduleEnd, cur.ScheduleTimezone, *changedBy,
		).Scan(&revisionID); err != nil {
			return err
		}
	} else {
		if err := tx.QueryRow(ctx, `
			INSERT INTO web_blocker_rule_revisions
				(rule_id,revision,name,enabled,action,severity,scope_all,
				 schedule_enabled,schedule_start,schedule_end,schedule_timezone)
			VALUES ($1::uuid,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11)
			RETURNING id`,
			cur.ID, nextRev, cur.Name, cur.Enabled, cur.Action, cur.Severity, cur.ScopeAll,
			cur.ScheduleEnabled, cur.ScheduleStart, cur.ScheduleEnd, cur.ScheduleTimezone,
		).Scan(&revisionID); err != nil {
			return err
		}
	}
	return replaceWebBlockerRevisionValues(ctx, tx, revisionID, cur.Domains, cur.Paths, cur.ScheduleDays)
}

func replaceWebBlockerRevisionValues(ctx context.Context, tx pgx.Tx, revisionID int64, domains, paths, days []string) error {
	for _, domain := range domains {
		domain = strings.TrimSpace(domain)
		if domain == "" {
			continue
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO web_blocker_revision_domains (revision_id, domain) VALUES ($1,$2)
			 ON CONFLICT (revision_id, domain) DO NOTHING`, revisionID, domain); err != nil {
			return err
		}
	}
	for _, path := range paths {
		path = strings.TrimSpace(path)
		if path == "" {
			continue
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO web_blocker_revision_paths (revision_id, path_pattern) VALUES ($1,$2)
			 ON CONFLICT (revision_id, path_pattern) DO NOTHING`, revisionID, path); err != nil {
			return err
		}
	}
	for _, day := range days {
		day = strings.ToLower(strings.TrimSpace(day))
		if day == "" {
			continue
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO web_blocker_revision_schedule_days (revision_id, day) VALUES ($1,$2)
			 ON CONFLICT (revision_id, day) DO NOTHING`, revisionID, day); err != nil {
			return err
		}
	}
	return nil
}

func (r *DlpRepo) SoftDeleteWebBlockerRule(ctx context.Context, id string) error {
	tag, err := r.pool.Exec(ctx, `UPDATE web_blocker_rules SET deleted_at=NOW() WHERE id=$1::uuid AND deleted_at IS NULL`, id)
	if err != nil {
		return err
	}
	if tag.RowsAffected() == 0 {
		return fmt.Errorf("web blocker rule not found")
	}
	return nil
}

// ── Policy versions ─────────────────────────────────────────────────────────

func (r *DlpRepo) InsertPolicyVersion(ctx context.Context, hash string, publishedBy *string) (*models.DlpPolicyVersion, error) {
	var row pgx.Row
	if publishedBy != nil && *publishedBy != "" {
		row = r.pool.QueryRow(ctx, `
			INSERT INTO dlp_policy_versions (bundle_hash,published_by)
			VALUES ($1,$2::uuid)
			RETURNING version,bundle_hash,published_at,published_by`, hash, *publishedBy)
	} else {
		row = r.pool.QueryRow(ctx, `
			INSERT INTO dlp_policy_versions (bundle_hash)
			VALUES ($1)
			RETURNING version,bundle_hash,published_at,published_by`, hash)
	}
	var v models.DlpPolicyVersion
	if err := row.Scan(&v.Version, &v.BundleHash, &v.PublishedAt, &v.PublishedBy); err != nil {
		return nil, err
	}
	return &v, nil
}

func (r *DlpRepo) PublishSnapshot(ctx context.Context, version int64, rules []models.DlpRule, webRules []models.WebBlockerRule, shadowDomains []string) error {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return err
	}
	defer tx.Rollback(ctx)
	for _, rule := range rules {
		alert := rule.Action == "alert" || rule.Action == "alert_block"
		_, err := tx.Exec(ctx, `
			INSERT INTO dlp_policy_rules
				(policy_version,rule_id,name,trigger,action,severity,alert,enabled)
			VALUES ($1,$2::uuid,$3,$4,$5,$6,$7,$8)`,
			version, rule.ID, rule.Name, rule.Trigger, rule.Action, rule.Severity, alert, rule.Enabled)
		if err != nil {
			return err
		}
		for _, ext := range cleanStrings(rule.Extensions, true) {
			if _, err := tx.Exec(ctx, `INSERT INTO dlp_policy_extensions (policy_version,rule_id,extension) VALUES ($1,$2::uuid,$3)`, version, rule.ID, ext); err != nil {
				return err
			}
		}
	}
	for _, rule := range webRules {
		alert := rule.Action == "alert" || rule.Action == "alert_block"
		_, err := tx.Exec(ctx, `
			INSERT INTO dlp_policy_rules
				(policy_version,rule_id,name,trigger,action,severity,alert,enabled,
				 schedule_enabled,schedule_start,schedule_end,schedule_timezone)
			VALUES ($1,$2::uuid,$3,'web_navigate',$4,$5,$6,$7,$8,$9,$10,$11)`,
			version, rule.ID, rule.Name, rule.Action, rule.Severity, alert, rule.Enabled,
			rule.ScheduleEnabled, rule.ScheduleStart, rule.ScheduleEnd, rule.ScheduleTimezone)
		if err != nil {
			return err
		}
		for _, domain := range cleanStrings(rule.Domains, true) {
			if _, err := tx.Exec(ctx, `INSERT INTO dlp_policy_domains (policy_version,rule_id,domain,source) VALUES ($1,$2::uuid,$3,'rule')`, version, rule.ID, domain); err != nil {
				return err
			}
		}
		for _, path := range cleanStrings(rule.Paths, false) {
			if _, err := tx.Exec(ctx, `INSERT INTO dlp_policy_paths (policy_version,rule_id,path_pattern) VALUES ($1,$2::uuid,$3)`, version, rule.ID, path); err != nil {
				return err
			}
		}
		for _, day := range cleanStrings(rule.ScheduleDays, true) {
			if _, err := tx.Exec(ctx, `INSERT INTO dlp_policy_schedule_days (policy_version,rule_id,day) VALUES ($1,$2::uuid,$3)`, version, rule.ID, day); err != nil {
				return err
			}
		}
	}
	for _, domain := range cleanStrings(shadowDomains, true) {
		if _, err := tx.Exec(ctx, `INSERT INTO dlp_policy_domains (policy_version,rule_id,domain,source) VALUES ($1,NULL,$2,'shadow')`, version, domain); err != nil {
			return err
		}
	}
	return tx.Commit(ctx)
}

func (r *DlpRepo) LatestPolicyVersion(ctx context.Context) (*models.DlpPolicyVersion, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT version,bundle_hash,published_at,published_by
		FROM dlp_policy_versions
		ORDER BY version DESC LIMIT 1`)
	var v models.DlpPolicyVersion
	err := row.Scan(&v.Version, &v.BundleHash, &v.PublishedAt, &v.PublishedBy)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (r *DlpRepo) GetPolicyVersion(ctx context.Context, version int64) (*models.DlpPolicyVersion, error) {
	row := r.pool.QueryRow(ctx, `
		SELECT version,bundle_hash,published_at,published_by
		FROM dlp_policy_versions WHERE version = $1`, version)
	var v models.DlpPolicyVersion
	err := row.Scan(&v.Version, &v.BundleHash, &v.PublishedAt, &v.PublishedBy)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &v, nil
}

func (r *DlpRepo) UpdatePolicyHash(ctx context.Context, version int64, hash string) error {
	_, err := r.pool.Exec(ctx, `UPDATE dlp_policy_versions SET bundle_hash=$2 WHERE version=$1`, version, hash)
	return err
}

func (r *DlpRepo) AssembleBundleJSON(ctx context.Context, version int64) (json.RawMessage, error) {
	header, err := r.GetPolicyVersion(ctx, version)
	if err != nil {
		return nil, err
	}
	if header == nil {
		return nil, fmt.Errorf("dlp policy version not found")
	}
	rows, err := r.pool.Query(ctx, `
		SELECT pr.rule_id::text,pr.name,pr.enabled,pr.trigger,pr.action,pr.severity,pr.alert,
		       pr.schedule_enabled,pr.schedule_start,pr.schedule_end,pr.schedule_timezone,
		       COALESCE((SELECT array_agg(d.domain ORDER BY d.domain) FROM dlp_policy_domains d
		                 WHERE d.policy_version=pr.policy_version AND d.rule_id=pr.rule_id AND d.source='rule'),ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.path_pattern ORDER BY p.path_pattern) FROM dlp_policy_paths p
		                 WHERE p.policy_version=pr.policy_version AND p.rule_id=pr.rule_id),ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(s.day ORDER BY s.day) FROM dlp_policy_schedule_days s
		                 WHERE s.policy_version=pr.policy_version AND s.rule_id=pr.rule_id),ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(e.extension ORDER BY e.extension) FROM dlp_policy_extensions e
		                 WHERE e.policy_version=pr.policy_version AND e.rule_id=pr.rule_id),ARRAY[]::text[])
		FROM dlp_policy_rules pr WHERE pr.policy_version=$1 ORDER BY pr.rule_id`, version)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	type wireRule struct {
		ID       string          `json:"id"`
		Name     string          `json:"name"`
		Enabled  bool            `json:"enabled"`
		Trigger  string          `json:"trigger"`
		Match    map[string]any  `json:"match"`
		Action   string          `json:"action"`
		Severity string          `json:"severity"`
		Scope    map[string]bool `json:"scope"`
		Alert    bool            `json:"alert"`
	}
	wireRules := make([]wireRule, 0)
	for rows.Next() {
		var id, name, trigger, action, severity, start, end, timezone string
		var enabled, alert, scheduleEnabled bool
		var domains, paths, days, extensions []string
		if err := rows.Scan(&id, &name, &enabled, &trigger, &action, &severity, &alert,
			&scheduleEnabled, &start, &end, &timezone, &domains, &paths, &days, &extensions); err != nil {
			return nil, err
		}
		match := map[string]any{}
		if len(extensions) > 0 {
			match["extensions"] = extensions
		}
		if trigger == "web_navigate" {
			match["domains"] = domains
			match["paths"] = paths
			match["schedule"] = map[string]any{
				"enabled": scheduleEnabled, "days": days, "start": start, "end": end, "timezone": timezone,
			}
		}
		wireRules = append(wireRules, wireRule{
			ID: id, Name: name, Enabled: enabled, Trigger: trigger, Match: match,
			Action: action, Severity: severity, Scope: map[string]bool{"all": true}, Alert: alert,
		})
	}
	if err := rows.Err(); err != nil {
		return nil, err
	}
	shadowDomains := make([]string, 0)
	shadowRows, err := r.pool.Query(ctx, `
		SELECT domain FROM dlp_policy_domains
		WHERE policy_version=$1 AND source='shadow' ORDER BY domain`, version)
	if err != nil {
		return nil, err
	}
	defer shadowRows.Close()
	for shadowRows.Next() {
		var domain string
		if err := shadowRows.Scan(&domain); err != nil {
			return nil, err
		}
		shadowDomains = append(shadowDomains, domain)
	}
	if err := shadowRows.Err(); err != nil {
		return nil, err
	}
	bundle := struct {
		PolicyVersion int64      `json:"policyVersion"`
		PublishedAt   string     `json:"publishedAt"`
		Rules         []wireRule `json:"rules"`
		ShadowDeny    struct {
			Domains    []string `json:"domains"`
			Publishers []string `json:"publishers"`
			AppNames   []string `json:"appNames"`
		} `json:"shadowDeny"`
	}{
		PolicyVersion: version,
		PublishedAt:   header.PublishedAt.UTC().Format(time.RFC3339),
		Rules:         wireRules,
	}
	bundle.ShadowDeny.Domains = shadowDomains
	bundle.ShadowDeny.Publishers = []string{}
	bundle.ShadowDeny.AppNames = []string{}
	return json.Marshal(bundle)
}

// ── Alerts ──────────────────────────────────────────────────────────────────

func (r *DlpRepo) UpsertAlerts(ctx context.Context, employeeID, machineID string, items []struct {
	ID            string
	RuleID        *string
	PolicyVersion int64
	Type          string
	Severity      string
	ActionTaken   string
	OccurredAt    time.Time
	Evidence      map[string]string
}) ([]string, []string, error) {
	accepted := make([]string, 0, len(items))
	rejected := make([]string, 0)
	for _, it := range items {
		if it.ID == "" || it.OccurredAt.IsZero() {
			rejected = append(rejected, it.ID)
			continue
		}
		sev := normalizeSeverity(it.Severity)
		taken := normalizeActionTaken(it.ActionTaken)
		tx, err := r.pool.Begin(ctx)
		if err != nil {
			rejected = append(rejected, it.ID)
			continue
		}
		_, err = tx.Exec(ctx, `
			INSERT INTO dlp_alerts (
				id, rule_id, policy_version, employee_id, machine_id,
				type, severity, action_taken, occurred_at, synced_at
			) VALUES (
				$1::uuid, $2::uuid, $3, $4, $5,
				$6, $7, $8, $9, NOW()
			)
			ON CONFLICT (id) DO UPDATE SET
				synced_at = NOW(),
				action_taken = EXCLUDED.action_taken`,
			it.ID, nullableUUID(it.RuleID), it.PolicyVersion, employeeID, machineID,
			it.Type, sev, taken, it.OccurredAt)
		if err != nil {
			_ = tx.Rollback(ctx)
			rejected = append(rejected, it.ID)
			continue
		}
		if _, err := tx.Exec(ctx, `DELETE FROM dlp_alert_evidence WHERE alert_id=$1::uuid`, it.ID); err != nil {
			_ = tx.Rollback(ctx)
			rejected = append(rejected, it.ID)
			continue
		}
		keys := make([]string, 0, len(it.Evidence))
		for key := range it.Evidence {
			keys = append(keys, key)
		}
		sort.Strings(keys)
		failed := false
		for _, rawKey := range keys {
			key := strings.TrimSpace(rawKey)
			if key == "" {
				continue
			}
			if _, err := tx.Exec(ctx, `
				INSERT INTO dlp_alert_evidence (alert_id,key,value)
				VALUES ($1::uuid,$2,$3)`, it.ID, key, it.Evidence[rawKey]); err != nil {
				failed = true
				break
			}
		}
		if failed {
			_ = tx.Rollback(ctx)
			rejected = append(rejected, it.ID)
			continue
		}
		if err := tx.Commit(ctx); err != nil {
			rejected = append(rejected, it.ID)
			continue
		}
		accepted = append(accepted, it.ID)
	}
	return accepted, rejected, nil
}

type DlpAlertListFilter struct {
	Search     string
	Status     string
	Severity   string
	Type       string
	EmployeeID string
	DateFrom   time.Time
	DateTo     time.Time
	Page       int
	PerPage    int
}

// alertWhereOpts controls which of the "self-referential" filters are
// applied to a given query.
type alertWhereOpts struct {
	// Exclude the status filter. Used by the status counts so the status
	// chips keep showing the FULL distribution across the other active
	// filters (search / severity / type / employee / date) even while one
	// status is selected — otherwise picking "Open" would zero out every
	// other chip and the user could not see what else is waiting.
	ExcludeStatus bool
	// Exclude the type filter. Used by the type list so the type dropdown
	// keeps offering every type available under the other filters, even
	// while one type is selected.
	ExcludeType bool
}

// buildAlertWhere builds the shared WHERE clause (plus its args) for the alert
// list, the status counts and the type list.
func buildAlertWhere(f DlpAlertListFilter, opts alertWhereOpts) (string, []any) {
	where := []string{"1=1"}
	args := []any{}
	n := 1
	if f.Search != "" {
		where = append(where, fmt.Sprintf(`(
			a.id::text ILIKE $%d OR a.employee_id ILIKE $%d OR COALESCE(e.name,'') ILIKE $%d
			OR a.type ILIKE $%d OR EXISTS (
				SELECT 1 FROM dlp_alert_evidence ae
				WHERE ae.alert_id=a.id AND (ae.key ILIKE $%d OR ae.value ILIKE $%d)
			)
		)`, n, n, n, n, n, n))
		args = append(args, "%"+f.Search+"%")
		n++
	}
	if !opts.ExcludeStatus && f.Status != "" {
		where = append(where, fmt.Sprintf("a.status = $%d", n))
		args = append(args, f.Status)
		n++
	}
	if f.Severity != "" {
		where = append(where, fmt.Sprintf("a.severity = $%d", n))
		args = append(args, f.Severity)
		n++
	}
	if !opts.ExcludeType && f.Type != "" {
		where = append(where, fmt.Sprintf("a.type = $%d", n))
		args = append(args, f.Type)
		n++
	}
	if f.EmployeeID != "" {
		// The alert's `employee_id` column holds the human code (EMP-XXXXX),
		// but callers may pass the employees-table UUID (the web's employee
		// picker URL key). Accept either so a deep link from another page
		// resolves to the same employee.
		where = append(where, fmt.Sprintf("(a.employee_id = $%d OR a.employee_id = COALESCE((SELECT e.employee_id FROM employees e WHERE e.id::text = $%d AND e.deleted_at IS NULL), ''))", n, n))
		args = append(args, f.EmployeeID)
		n++
	}
	if !f.DateFrom.IsZero() {
		where = append(where, fmt.Sprintf("a.occurred_at >= $%d", n))
		args = append(args, f.DateFrom)
		n++
	}
	if !f.DateTo.IsZero() {
		where = append(where, fmt.Sprintf("a.occurred_at <= $%d", n))
		args = append(args, f.DateTo)
		n++
	}
	return strings.Join(where, " AND "), args
}

func (r *DlpRepo) ListAlerts(ctx context.Context, f DlpAlertListFilter) ([]models.DlpAlert, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	wsql, args := buildAlertWhere(f, alertWhereOpts{})

	var total int
	countSQL := `SELECT COUNT(*) FROM dlp_alerts a LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL WHERE ` + wsql
	if err := r.pool.QueryRow(ctx, countSQL, args...).Scan(&total); err != nil {
		return nil, 0, err
	}

	offset := (f.Page - 1) * f.PerPage
	limitArg := len(args) + 1
	offsetArg := len(args) + 2
	args = append(args, f.PerPage, offset)
	listSQL := fmt.Sprintf(`
		SELECT a.id::text, a.rule_id::text, a.policy_version, a.employee_id, a.machine_id,
		       a.type, a.severity, a.action_taken, a.occurred_at,
		       a.status, a.assigned_to, a.notes, a.synced_at, a.created_at, a.updated_at,
		       COALESCE(e.name, '')
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE %s
		ORDER BY a.occurred_at DESC
		LIMIT $%d OFFSET $%d`, wsql, limitArg, offsetArg)

	rows, err := r.pool.Query(ctx, listSQL, args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()

	out := make([]models.DlpAlert, 0)
	for rows.Next() {
		var a models.DlpAlert
		var ruleID *string
		if err := rows.Scan(
			&a.ID, &ruleID, &a.PolicyVersion, &a.EmployeeID, &a.MachineID,
			&a.Type, &a.Severity, &a.ActionTaken, &a.OccurredAt,
			&a.Status, &a.AssignedTo, &a.Notes, &a.SyncedAt, &a.CreatedAt, &a.UpdatedAt,
			&a.EmployeeName,
		); err != nil {
			return nil, 0, err
		}
		a.RuleID = ruleID
		a.Evidence, err = loadAlertEvidence(ctx, r.pool, a.ID)
		if err != nil {
			return nil, 0, err
		}
		out = append(out, a)
	}
	return out, total, rows.Err()
}

// AlertStatusCounts returns the open / investigating / resolved /
// false-positive distribution for the CURRENTLY ACTIVE FILTERS — except the
// status filter itself, so the status chips keep showing the full
// distribution across the other active filters (search / severity / type /
// employee / date) even while one status is selected.
func (r *DlpRepo) AlertStatusCounts(ctx context.Context, f DlpAlertListFilter) (open, investigating, resolved, falsePositive int, err error) {
	wsql, args := buildAlertWhere(f, alertWhereOpts{ExcludeStatus: true})
	countSQL := `
		SELECT
			COUNT(*) FILTER (WHERE a.status = 'open'),
			COUNT(*) FILTER (WHERE a.status = 'investigating'),
			COUNT(*) FILTER (WHERE a.status = 'resolved'),
			COUNT(*) FILTER (WHERE a.status = 'false_positive')
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE ` + wsql
	if err = r.pool.QueryRow(ctx, countSQL, args...).Scan(&open, &investigating, &resolved, &falsePositive); err != nil {
		return 0, 0, 0, 0, err
	}
	return
}

// AlertTypeCounts returns the distinct alert types present under the current
// filters — except the type filter itself (and the status filter), so the type
// dropdown keeps offering every available type even while one is selected.
func (r *DlpRepo) AlertTypeCounts(ctx context.Context, f DlpAlertListFilter) ([]string, error) {
	wsql, args := buildAlertWhere(f, alertWhereOpts{ExcludeStatus: true, ExcludeType: true})
	rows, err := r.pool.Query(ctx, `
		SELECT DISTINCT a.type
		FROM dlp_alerts a
		LEFT JOIN employees e ON e.employee_id = a.employee_id AND e.deleted_at IS NULL
		WHERE `+wsql+` AND a.type <> ''
		ORDER BY a.type`, args...)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := []string{}
	for rows.Next() {
		var t string
		if err := rows.Scan(&t); err != nil {
			return nil, err
		}
		out = append(out, t)
	}
	return out, rows.Err()
}

// UpdateAlertPatch carries the optional triage fields for a single save.
type UpdateAlertPatch struct {
	Status     *string
	AssignedTo *string
	Notes      *string
	// ChangedBy is the admin users.id (UUID string); may be empty.
	ChangedBy     string
	ChangedByName string
}

// UpdateAlert applies triage fields and appends one dlp_alert_events row per
// field that actually changed. Open status always clears assigned_to.
func (r *DlpRepo) UpdateAlert(ctx context.Context, id string, patch UpdateAlertPatch) (*models.DlpAlert, error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return nil, err
	}
	defer tx.Rollback(ctx)

	var cur models.DlpAlert
	var ruleID *string
	err = tx.QueryRow(ctx, `
		SELECT id::text, rule_id::text, policy_version, employee_id, machine_id,
		       type, severity, action_taken, occurred_at,
		       status, assigned_to, notes, synced_at, created_at, updated_at
		FROM dlp_alerts WHERE id = $1::uuid
		FOR UPDATE`, id).Scan(
		&cur.ID, &ruleID, &cur.PolicyVersion, &cur.EmployeeID, &cur.MachineID,
		&cur.Type, &cur.Severity, &cur.ActionTaken, &cur.OccurredAt,
		&cur.Status, &cur.AssignedTo, &cur.Notes, &cur.SyncedAt, &cur.CreatedAt, &cur.UpdatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	cur.RuleID = ruleID

	nextStatus := cur.Status
	if patch.Status != nil {
		nextStatus = *patch.Status
	}
	nextAssigned := cur.AssignedTo
	if patch.AssignedTo != nil {
		nextAssigned = *patch.AssignedTo
	}
	// Open has no assignee — always clear on that status.
	if nextStatus == "open" {
		nextAssigned = ""
	}
	nextNotes := cur.Notes
	if patch.Notes != nil {
		nextNotes = *patch.Notes
	}

	type change struct{ field, oldV, newV string }
	var changes []change
	if nextStatus != cur.Status {
		changes = append(changes, change{"status", cur.Status, nextStatus})
	}
	if nextAssigned != cur.AssignedTo {
		changes = append(changes, change{"assigned_to", cur.AssignedTo, nextAssigned})
	}
	if nextNotes != cur.Notes {
		changes = append(changes, change{"notes", cur.Notes, nextNotes})
	}

	row := tx.QueryRow(ctx, `
		UPDATE dlp_alerts SET
			status = $2,
			assigned_to = $3,
			notes = $4
		WHERE id = $1::uuid
		RETURNING id::text, rule_id::text, policy_version, employee_id, machine_id,
		          type, severity, action_taken, occurred_at,
		          status, assigned_to, notes, synced_at, created_at, updated_at`,
		id, nextStatus, nextAssigned, nextNotes)
	var a models.DlpAlert
	err = row.Scan(
		&a.ID, &ruleID, &a.PolicyVersion, &a.EmployeeID, &a.MachineID,
		&a.Type, &a.Severity, &a.ActionTaken, &a.OccurredAt,
		&a.Status, &a.AssignedTo, &a.Notes, &a.SyncedAt, &a.CreatedAt, &a.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	a.RuleID = ruleID
	a.Evidence, err = loadAlertEvidence(ctx, tx, id)
	if err != nil {
		return nil, err
	}

	if len(changes) > 0 {
		now := time.Now().UTC()
		for _, ch := range changes {
			if _, err := tx.Exec(ctx, `
				INSERT INTO dlp_alert_events
					(alert_id, changed_by, changed_by_name, field, old_value, new_value, created_at)
				VALUES ($1::uuid, NULLIF($2, '')::uuid, $3, $4, $5, $6, $7)`,
				id, patch.ChangedBy, patch.ChangedByName, ch.field, ch.oldV, ch.newV, now,
			); err != nil {
				return nil, err
			}
		}
	}

	if err := tx.Commit(ctx); err != nil {
		return nil, err
	}
	return &a, nil
}

// ListAlertEvents returns the triage audit trail newest-first.
func (r *DlpRepo) ListAlertEvents(ctx context.Context, alertID string) ([]models.DlpAlertEvent, error) {
	rows, err := r.pool.Query(ctx, `
		SELECT id, alert_id::text, changed_by::text, changed_by_name,
		       field, old_value, new_value, created_at
		FROM dlp_alert_events
		WHERE alert_id = $1::uuid
		ORDER BY created_at DESC, id DESC`, alertID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	out := make([]models.DlpAlertEvent, 0)
	for rows.Next() {
		var e models.DlpAlertEvent
		var changedBy *string
		if err := rows.Scan(
			&e.ID, &e.AlertID, &changedBy, &e.ChangedByName,
			&e.Field, &e.OldValue, &e.NewValue, &e.CreatedAt,
		); err != nil {
			return nil, err
		}
		e.ChangedBy = changedBy
		out = append(out, e)
	}
	return out, rows.Err()
}

// UserDisplayName resolves an admin user's display name (empty if missing).
func (r *DlpRepo) UserDisplayName(ctx context.Context, userID string) string {
	if userID == "" {
		return ""
	}
	var name string
	err := r.pool.QueryRow(ctx,
		`SELECT COALESCE(name, '') FROM users WHERE id = $1::uuid AND deleted_at IS NULL`,
		userID,
	).Scan(&name)
	if err != nil {
		return ""
	}
	return name
}

// ── Shadow IT ───────────────────────────────────────────────────────────────

type ShadowItListFilter struct {
	Search     string
	AuthStatus string
	Page       int
	PerPage    int
}

func (r *DlpRepo) ListShadowItApps(ctx context.Context, f ShadowItListFilter) ([]models.ShadowItApp, int, error) {
	if f.Page < 1 {
		f.Page = 1
	}
	if f.PerPage < 1 || f.PerPage > 100 {
		f.PerPage = 30
	}
	where := []string{"deleted_at IS NULL"}
	args := []any{}
	n := 1
	if f.Search != "" {
		where = append(where, fmt.Sprintf("name ILIKE $%d", n))
		args = append(args, "%"+f.Search+"%")
		n++
	}
	if f.AuthStatus != "" {
		where = append(where, fmt.Sprintf("auth_status = $%d", n))
		args = append(args, f.AuthStatus)
		n++
	}
	wsql := strings.Join(where, " AND ")
	var total int
	if err := r.pool.QueryRow(ctx, `SELECT COUNT(*) FROM shadow_it_apps WHERE `+wsql, args...).Scan(&total); err != nil {
		return nil, 0, err
	}
	offset := (f.Page - 1) * f.PerPage
	args = append(args, f.PerPage, offset)
	rows, err := r.pool.Query(ctx, fmt.Sprintf(`
		SELECT id, name, name_normalized, kind, auth_status, risk,
		       first_detected_at, user_count, last_seen_at, created_at, updated_at
		FROM shadow_it_apps WHERE %s
		ORDER BY last_seen_at DESC
		LIMIT $%d OFFSET $%d`, wsql, n, n+1), args...)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	out := make([]models.ShadowItApp, 0)
	for rows.Next() {
		var a models.ShadowItApp
		if err := rows.Scan(
			&a.ID, &a.Name, &a.NameNormalized, &a.Kind, &a.AuthStatus, &a.Risk,
			&a.FirstDetectedAt, &a.UserCount, &a.LastSeenAt, &a.CreatedAt, &a.UpdatedAt,
		); err != nil {
			return nil, 0, err
		}
		out = append(out, a)
	}
	return out, total, rows.Err()
}

func (r *DlpRepo) ShadowItStatusCounts(ctx context.Context) (approved, unauthorized, underReview int, err error) {
	err = r.pool.QueryRow(ctx, `
		SELECT
			COUNT(*) FILTER (WHERE auth_status = 'approved'),
			COUNT(*) FILTER (WHERE auth_status = 'unauthorized'),
			COUNT(*) FILTER (WHERE auth_status = 'under_review')
		FROM shadow_it_apps WHERE deleted_at IS NULL`).Scan(&approved, &unauthorized, &underReview)
	return
}

func (r *DlpRepo) UpdateShadowItApp(ctx context.Context, id int64, authStatus, risk *string) (*models.ShadowItApp, error) {
	row := r.pool.QueryRow(ctx, `
		UPDATE shadow_it_apps SET
			auth_status = COALESCE($2, auth_status),
			risk = COALESCE($3, risk)
		WHERE id = $1 AND deleted_at IS NULL
		RETURNING id, name, name_normalized, kind, auth_status, risk,
		          first_detected_at, user_count, last_seen_at, created_at, updated_at`,
		id, authStatus, risk)
	var a models.ShadowItApp
	err := row.Scan(
		&a.ID, &a.Name, &a.NameNormalized, &a.Kind, &a.AuthStatus, &a.Risk,
		&a.FirstDetectedAt, &a.UserCount, &a.LastSeenAt, &a.CreatedAt, &a.UpdatedAt,
	)
	if err == pgx.ErrNoRows {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return &a, nil
}

func (r *DlpRepo) ListUnauthorizedShadowDeny(ctx context.Context) (domains, appNames []string, err error) {
	rows, err := r.pool.Query(ctx, `
		SELECT name, kind FROM shadow_it_apps
		WHERE deleted_at IS NULL AND auth_status = 'unauthorized'`)
	if err != nil {
		return nil, nil, err
	}
	defer rows.Close()
	for rows.Next() {
		var name, kind string
		if err := rows.Scan(&name, &kind); err != nil {
			return nil, nil, err
		}
		if kind == "website" || kind == "saas" {
			domains = append(domains, strings.ToLower(name))
		} else {
			appNames = append(appNames, name)
		}
	}
	return domains, appNames, rows.Err()
}

// ── Removal challenges ──────────────────────────────────────────────────────

func (r *DlpRepo) InsertRemovalChallenge(ctx context.Context, machineID, employeeID, tokenHash, purpose string, expiresAt time.Time, issuedBy *string) (string, error) {
	var id string
	if issuedBy != nil && *issuedBy != "" {
		err := r.pool.QueryRow(ctx, `
			INSERT INTO removal_challenges (machine_id, employee_id, token_hash, purpose, expires_at, issued_by)
			VALUES ($1, $2, $3, $4, $5, $6::uuid)
			RETURNING id::text`, machineID, employeeID, tokenHash, purpose, expiresAt, *issuedBy).Scan(&id)
		return id, err
	}
	err := r.pool.QueryRow(ctx, `
		INSERT INTO removal_challenges (machine_id, employee_id, token_hash, purpose, expires_at)
		VALUES ($1, $2, $3, $4, $5)
		RETURNING id::text`, machineID, employeeID, tokenHash, purpose, expiresAt).Scan(&id)
	return id, err
}

// ConsumeRemovalChallenge validates hash for the calling machine and marks used.
func (r *DlpRepo) ConsumeRemovalChallenge(ctx context.Context, machineID, tokenHash, purpose string) (valid bool, storedPurpose string, err error) {
	tx, err := r.pool.Begin(ctx)
	if err != nil {
		return false, "", err
	}
	defer tx.Rollback(ctx)

	var id, p string
	err = tx.QueryRow(ctx, `
		SELECT id::text, purpose FROM removal_challenges
		WHERE token_hash = $1 AND machine_id = $2
		  AND used_at IS NULL AND expires_at > NOW()
		FOR UPDATE`, tokenHash, machineID).Scan(&id, &p)
	if err == pgx.ErrNoRows {
		return false, "", nil
	}
	if err != nil {
		return false, "", err
	}
	if purpose != "" && p != "both" && p != purpose {
		return false, p, nil
	}
	_, err = tx.Exec(ctx, `UPDATE removal_challenges SET used_at = NOW() WHERE id = $1::uuid`, id)
	if err != nil {
		return false, "", err
	}
	if err := tx.Commit(ctx); err != nil {
		return false, "", err
	}
	return true, p, nil
}

// ── helpers ─────────────────────────────────────────────────────────────────

func scanDlpRules(rows pgx.Rows) ([]models.DlpRule, error) {
	out := make([]models.DlpRule, 0)
	for rows.Next() {
		rule, err := scanDlpRule(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, *rule)
	}
	return out, rows.Err()
}

type scannable interface {
	Scan(dest ...any) error
}

func scanDlpRule(row scannable) (*models.DlpRule, error) {
	var rule models.DlpRule
	err := row.Scan(
		&rule.ID, &rule.Name, &rule.Enabled, &rule.Trigger, &rule.Action,
		&rule.Severity, &rule.ScopeAll, &rule.Extensions, &rule.Patterns,
		&rule.CreatedAt, &rule.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &rule, nil
}

type dlpQuerier interface {
	Query(context.Context, string, ...any) (pgx.Rows, error)
	QueryRow(context.Context, string, ...any) pgx.Row
}

func getDlpRule(ctx context.Context, q dlpQuerier, id string) (*models.DlpRule, error) {
	return scanDlpRule(q.QueryRow(ctx, `
		SELECT r.id::text,r.name,r.enabled,r.trigger,r.action,r.severity,r.scope_all,
		       COALESCE((SELECT array_agg(e.extension ORDER BY e.extension) FROM dlp_rule_extensions e WHERE e.rule_id=r.id),ARRAY[]::text[]),
		       COALESCE((SELECT array_agg(p.pattern ORDER BY p.pattern) FROM dlp_rule_patterns p WHERE p.rule_id=r.id),ARRAY[]::text[]),
		       r.created_at,r.updated_at
		FROM dlp_rules r WHERE r.id=$1::uuid AND r.deleted_at IS NULL`, id))
}

func replaceRuleValues(ctx context.Context, tx pgx.Tx, id string, extensions, patterns []string) error {
	if _, err := tx.Exec(ctx, `DELETE FROM dlp_rule_extensions WHERE rule_id=$1::uuid`, id); err != nil {
		return err
	}
	if _, err := tx.Exec(ctx, `DELETE FROM dlp_rule_patterns WHERE rule_id=$1::uuid`, id); err != nil {
		return err
	}
	for _, extension := range cleanStrings(extensions, true) {
		if _, err := tx.Exec(ctx, `INSERT INTO dlp_rule_extensions (rule_id,extension) VALUES ($1::uuid,$2)`, id, extension); err != nil {
			return err
		}
	}
	for _, pattern := range cleanStrings(patterns, false) {
		if _, err := tx.Exec(ctx, `INSERT INTO dlp_rule_patterns (rule_id,pattern) VALUES ($1::uuid,$2)`, id, pattern); err != nil {
			return err
		}
	}
	return nil
}

func scanWebBlockerRules(rows pgx.Rows) ([]models.WebBlockerRule, error) {
	out := make([]models.WebBlockerRule, 0)
	for rows.Next() {
		rule, err := scanWebBlockerRule(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, *rule)
	}
	return out, rows.Err()
}

func scanWebBlockerRule(row scannable) (*models.WebBlockerRule, error) {
	var rule models.WebBlockerRule
	err := row.Scan(
		&rule.ID, &rule.Name, &rule.Enabled, &rule.Action, &rule.Severity, &rule.ScopeAll,
		&rule.Domains, &rule.Paths, &rule.ScheduleEnabled, &rule.ScheduleStart,
		&rule.ScheduleEnd, &rule.ScheduleTimezone, &rule.ScheduleDays,
		&rule.CreatedAt, &rule.UpdatedAt,
	)
	if err != nil {
		return nil, err
	}
	return &rule, nil
}

func getWebBlockerRule(ctx context.Context, q dlpQuerier, id string) (*models.WebBlockerRule, error) {
	return scanWebBlockerRule(q.QueryRow(ctx, webBlockerSelect+` WHERE r.id=$1::uuid AND r.deleted_at IS NULL`, id))
}

func replaceWebBlockerValues(ctx context.Context, tx pgx.Tx, id string, domains, paths, days []string) error {
	for _, table := range []string{"web_blocker_domains", "web_blocker_paths", "web_blocker_schedule_days"} {
		if _, err := tx.Exec(ctx, `DELETE FROM `+table+` WHERE rule_id=$1::uuid`, id); err != nil {
			return err
		}
	}
	for _, domain := range cleanStrings(domains, true) {
		if _, err := tx.Exec(ctx, `INSERT INTO web_blocker_domains (rule_id,domain) VALUES ($1::uuid,$2)`, id, domain); err != nil {
			return err
		}
	}
	for _, path := range cleanStrings(paths, false) {
		if _, err := tx.Exec(ctx, `INSERT INTO web_blocker_paths (rule_id,path_pattern) VALUES ($1::uuid,$2)`, id, path); err != nil {
			return err
		}
	}
	for _, day := range cleanStrings(days, true) {
		if _, err := tx.Exec(ctx, `INSERT INTO web_blocker_schedule_days (rule_id,day) VALUES ($1::uuid,$2)`, id, day); err != nil {
			return err
		}
	}
	return nil
}

func cleanStrings(values []string, lower bool) []string {
	seen := make(map[string]struct{}, len(values))
	out := make([]string, 0, len(values))
	for _, value := range values {
		value = strings.TrimSpace(value)
		if lower {
			value = strings.ToLower(value)
		}
		if value == "" {
			continue
		}
		if _, ok := seen[value]; ok {
			continue
		}
		seen[value] = struct{}{}
		out = append(out, value)
	}
	sort.Strings(out)
	return out
}

func loadAlertEvidence(ctx context.Context, q dlpQuerier, alertID string) (map[string]string, error) {
	rows, err := q.Query(ctx, `
		SELECT key,value FROM dlp_alert_evidence
		WHERE alert_id=$1::uuid ORDER BY key`, alertID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	evidence := make(map[string]string)
	for rows.Next() {
		var key, value string
		if err := rows.Scan(&key, &value); err != nil {
			return nil, err
		}
		evidence[key] = value
	}
	return evidence, rows.Err()
}

func nullableUUID(s *string) any {
	if s == nil || *s == "" {
		return nil
	}
	return *s
}

func normalizeSeverity(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "critical", "high", "medium", "low":
		return strings.ToLower(s)
	default:
		return "medium"
	}
}

func normalizeActionTaken(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "alerted", "blocked", "blocked_failed":
		return strings.ToLower(s)
	case "alert":
		return "alerted"
	case "block", "alert_block", "alert+block":
		return "blocked"
	default:
		return "alerted"
	}
}

// HashRemovalToken returns hex(sha256(raw)).
func HashRemovalToken(raw string) string {
	sum := sha256.Sum256([]byte(raw))
	return hex.EncodeToString(sum[:])
}
