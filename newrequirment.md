I think **Productivity Rules should become its own proper configuration module**. Your existing Applications/Websites classification becomes the **input**, while Productivity Rules becomes the **logic that turns activity into scores, percentages, flags, and reports**.

Your current tracker is already well positioned for this: you have `monitoring_types` with Productive/Neutral/Unproductive, application and website classification, `app_sessions`, `app_items`, browser journey tracking, and foreground/background time.

Below is the architecture I would use.

---

# 1. The big picture

Separate the system into **4 layers**:

```text
┌───────────────────────────────────────────┐
│              CONFIGURATION                │
│                                           │
│ Applications                              │
│ Websites                                  │
│ Categories                                │
│ Productivity Rules                        │
└────────────────────┬──────────────────────┘
                     │
                     ▼
┌───────────────────────────────────────────┐
│             ACTIVITY DATA                 │
│                                           │
│ App Sessions                              │
│ Web Activity                              │
│ Foreground Time                           │
│ Idle Time                                 │
└────────────────────┬──────────────────────┘
                     │
                     ▼
┌───────────────────────────────────────────┐
│          PRODUCTIVITY ENGINE              │
│                                           │
│ Resolve Classification                    │
│ Apply Rules                               │
│ Calculate Time                            │
│ Calculate Score                           │
│ Calculate Percentage                      │
│ Generate Flags                            │
└────────────────────┬──────────────────────┘
                     │
                     ▼
┌───────────────────────────────────────────┐
│                 REPORTING                 │
│                                           │
│ Employee                                  │
│ Team                                      │
│ Department                                │
│ Organization                              │
│ Daily / Weekly / Monthly                  │
└───────────────────────────────────────────┘
```

This separation is the key.

---

# 2. What each module means

## Applications & Websites

Answers:

> **"What type of activity is this?"**

Example:

```text
VS Code
→ Productive

GitHub
→ Productive

YouTube
→ Unproductive

Google
→ Neutral
```

Your existing Configuration system already does this.

---

# 3. Productivity Rules

Answers:

> **"What should we do with that classification?"**

For example:

```text
Productive = +1
Neutral = 0
Unproductive = -1
```

or:

```text
Productive time / classified time
```

or:

```text
If unproductive activity > 30 minutes
→ warning
```

or:

```text
Productivity >= 80%
→ Excellent
```

So:

### Applications/Websites

```text
INPUT
```

### Productivity Rules

```text
LOGIC
```

### Productivity Engine

```text
CALCULATION
```

### Dashboard

```text
OUTPUT
```

---

# 4. Recommended sidebar

I would structure your Configuration section like this:

```text
Configuration
│
├── Applications
├── Websites
├── Categories & Types
└── Productivity Rules
```

I would **not** put Productivity Rules under Monitoring.

It is configuration/business logic, not raw monitoring.

Your current sidebar already has Applications, Websites, and Categories & Types under Configuration, so this fits naturally.

---

# 5. Productivity Rules page

Make it a serious configuration page rather than just a few inputs.

Something like:

```text
Productivity Rules

Configure how employee activity is converted
into productivity metrics and performance levels.
```

Then sections.

---

# 6. Section 1 — Calculation Method

This controls the primary formula.

Example:

```text
Calculation Method

(•) Productive / Classified Time

( ) Productive / Active Time

( ) Weighted Score
```

I recommend initially supporting:

### Method 1 — Productive / Classified

```text
Productive Time
──────────────────── × 100
Classified Time
```

This should be your default.

---

# 7. Section 2 — Activity weights

Allow:

```text
Productive
[ 100 ]

Neutral
[ 0 ]

Unproductive
[ -100 ]
```

But internally I'd normalize these.

For example:

```text
Productive = 1
Neutral = 0
Unproductive = -1
```

The UI can show percentages or points, but the engine should have a well-defined numeric representation.

---

# 8. Why weights are useful

Later you may want:

```text
Productive = +1.0
Neutral = 0
Unproductive = -1.0
```

Or a company might want:

```text
Productive = +1.0
Neutral = +0.2
Unproductive = -1.0
```

Or:

```text
Productive = +100
Neutral = +10
Unproductive = -100
```

You shouldn't have to rewrite your calculation engine.

The rule configuration controls it.

---

# 9. But don't overcomplicate v1

For version 1, I recommend:

```text
Productive
Neutral
Unproductive
```

with fixed semantics.

Then add custom weights later.

The first version should be predictable.

---

# 10. Section 3 — Time source

This is extremely important.

Give the administrator:

```text
Time Calculation

Foreground Time
(•) Use foreground time

Session Duration
( ) Use total session duration
```

I strongly recommend:

> **Foreground time**

Your tracker already records foreground/background seconds per application session.

Why?

Suppose:

```text
VS Code opened: 8:00
VS Code closed: 12:00
```

That's 4 hours.

But the employee may only have actively used it for:

```text
2h 30m
```

Using four hours would inflate productivity.

---

# 11. Idle time

Add:

```text
Idle Time

[✓] Exclude idle time
```

For example:

```text
Computer active: 8h
Idle: 2h
Foreground activity: 6h
```

The productivity calculation should normally use:

```text
6h
```

not:

```text
8h
```

---

# 12. Section 4 — Unclassified activity

This is very important.

An application/site can exist without a type:

```text
VS Code → Productive
YouTube → Unproductive
SomeNewSite.com → NULL
```

The system needs to know what to do with the unknown one.

Give three options:

```text
Unclassified Activity

(•) Exclude from productivity calculation

( ) Treat as Neutral

( ) Treat as Unproductive
```

### Recommended default

```text
Exclude
```

Because:

> Unknown does not mean neutral, and unknown does not mean bad.

This prevents the system from making assumptions.

Your current UI already has an Unclassified concept/status, so this fits the existing architecture.

---

# 13. Section 5 — Productivity thresholds

This is where the system turns a number into a human-readable performance level.

Example:

```text
Performance Levels

Excellent
80 – 100%

Good
60 – 79%

Average
40 – 59%

Low
0 – 39%
```

You can allow admins to configure these.

---

# 14. Don't confuse score with performance level

These are different.

### Score

```text
82.5%
```

### Performance level

```text
Excellent
```

The engine calculates:

```text
82.5
```

Then rules resolve:

```text
82.5 → Excellent
```

---

# 15. Section 6 — Focus Score

I recommend making Focus Score a separate metric.

Formula:

```text
Productive Foreground Time
──────────────────────────── × 100
Total Foreground Time
```

Example:

```text
Productive = 5h
Neutral = 1h
Unproductive = 1h
Unclassified = 1h

Foreground = 8h
```

Focus:

```text
5 / 8 × 100
= 62.5%
```

Productivity:

```text
5 / 7 × 100
= 71.4%
```

These tell different stories.

---

# 16. Why you need both

### Productivity

> How productive was the classified activity?

### Focus

> How much of the employee's active time was productive?

This gives your tracker much richer reporting.

---

# 17. Section 7 — Unproductive thresholds

Now you can add behavior rules.

Example:

```text
Unproductive Activity Alerts

Enable alerts
[✓]

Threshold
[ 30 ] minutes

Period
[ Daily ▼ ]
```

Meaning:

```text
YouTube + Facebook + Instagram
> 30 minutes/day
```

could generate:

```text
Unproductive Activity Warning
```

---

# 18. Important: don't make this automatically affect score

This is a separate rule.

For example:

```text
Unproductive = 40 minutes
```

The score already reflects that.

The threshold should only create:

```text
Flag
Warning
Insight
```

Don't subtract another 10% just because a threshold was crossed.

Otherwise you double-penalize the same behavior.

---

# 19. Section 8 — Minimum activity duration

Add:

```text
Minimum Activity Duration

[ 10 ] seconds
```

Why?

Activity can contain tiny events:

```text
YouTube: 2 sec
Chrome: 3 sec
Settings: 4 sec
```

These shouldn't necessarily influence reporting.

You can define:

```text
If duration < minimum
→ ignore
```

I would make the default conservative.

---

# 20. Section 9 — Browser handling

This deserves its own rule.

Your tracker records browser sessions and individual website activity.

You must avoid:

```text
Chrome = 2h
GitHub = 1h
YouTube = 1h

Total = 4h ❌
```

Because:

```text
GitHub + YouTube = Chrome's 2h
```

The rule should be:

```text
Browser Container Time

(•) Use website/tab time when available
( ) Use browser application time
```

Recommended:

```text
Use website/tab time
```

---

# 21. The classification resolution hierarchy

This is the heart of the engine.

For an activity:

```text
Is this browser activity?
        │
       YES
        ↓
Does domain exist?
        │
       YES
        ↓
Find monitoring_sites
        │
        ├── classified → use website type
        │
        └── unclassified → unclassified
```

For normal applications:

```text
Activity
 ↓
installed_applications
 ↓
type_id
 ↓
monitoring_types
```

---

# 22. Website always wins inside browser activity

Example:

```text
Chrome → Productive?
```

Don't use Chrome's classification for its tabs.

Instead:

```text
Chrome
  ├── github.com → Productive
  ├── youtube.com → Unproductive
  └── google.com → Neutral
```

That is the correct model.

Your current Web Activity architecture already tracks domain and URL-level activity, so this can be built on top of it.

---

# 23. Productivity Engine

Now create a backend service conceptually like:

```text
ProductivityEngine
```

It should have several stages.

```text
Raw Activity
     ↓
Normalize
     ↓
Resolve Classification
     ↓
Resolve Time
     ↓
Apply Productivity Rules
     ↓
Aggregate
     ↓
Calculate Metrics
     ↓
Generate Flags
```

---

# 24. Stage 1 — Normalize activity

Convert everything into a common internal structure:

```text
ActivityRecord
```

Something like:

```text
employeeId
timestampStart
timestampEnd
durationSeconds

source
applicationId
websiteId
domain

classification
typeId

foreground
browser
```

The exact DTO can be designed around your existing schema.

---

# 25. Stage 2 — Resolve classification

For application:

```text
installed_application.type_id
```

For website:

```text
monitoring_site.type_id
```

Then resolve:

```text
Productive
Neutral
Unproductive
Unclassified
```

---

# 26. Stage 3 — Resolve usable time

Use:

```text
foreground_seconds
```

where available.

Then:

```text
duration
- idle
- excluded intervals
```

according to rules.

---

# 27. Stage 4 — Apply rule configuration

Example:

```text
Rule:
Unclassified = EXCLUDE
```

Therefore:

```text
Productive  → include
Neutral     → include
Unproductive → include
Unclassified → exclude
```

---

# 28. Stage 5 — Aggregate

For one employee:

```text
productiveSeconds
neutralSeconds
unproductiveSeconds
unclassifiedSeconds
```

Also:

```text
totalForegroundSeconds
classifiedSeconds
```

And optionally:

```text
applicationProductiveSeconds
websiteProductiveSeconds
```

---

# 29. Stage 6 — Calculate metrics

### Productive percentage

```text
productive / classified × 100
```

### Neutral percentage

```text
neutral / classified × 100
```

### Unproductive percentage

```text
unproductive / classified × 100
```

### Focus score

```text
productive / foreground × 100
```

---

# 30. Example

Suppose:

```text
Foreground = 8h

Productive = 5h 30m
Neutral = 40m
Unproductive = 50m
Unclassified = 1h
```

Classified:

```text
5h30 + 40m + 50m
= 7h
```

Productivity:

```text
5.5 / 7 × 100
= 78.57%
```

Focus:

```text
5.5 / 8 × 100
= 68.75%
```

Dashboard:

```text
Productivity      78.6%
Focus              68.8%

Productive         5h 30m
Neutral              40m
Unproductive         50m
Unclassified         1h
```

---

# 31. Daily aggregation

Your API should support:

```text
Today
Yesterday
Last 7 days
Last 30 days
Custom range
```

Your existing App Usage/Web Activity pages already use server-side `dateFrom/dateTo` filtering, so productivity should follow the same filtering conventions.

---

# 32. Don't average percentages

This is a common mistake.

Don't do:

```text
Monday = 90%
Tuesday = 50%
Wednesday = 80%

Average = 73.3%
```

Instead calculate:

```text
SUM(productive_seconds)
────────────────────────
SUM(classified_seconds)
```

Then calculate the percentage.

This gives long days the appropriate weight.

---

# 33. Department calculation

Same principle.

Suppose:

```text
Employee A
Productive = 8h
Classified = 10h

Employee B
Productive = 1h
Classified = 2h
```

Department:

```text
9h / 12h × 100
= 75%
```

Not:

```text
(80% + 50%) / 2
= 65%
```

---

# 34. Organization calculation

Exactly the same.

```text
All employees productive seconds
────────────────────────────────
All employees classified seconds
× 100
```

This gives you:

```text
Company Productivity
```

without introducing statistical distortion.

---

# 35. Rule versioning

This is something I'd strongly recommend.

Suppose today you have:

```text
Productivity Rules v1
```

Tomorrow admin changes:

```text
Unclassified = Neutral
```

What happens to yesterday's report?

You don't want historical numbers silently changing.

Therefore:

```text
Rule Set
│
├── version 1
├── version 2
└── version 3
```

Each calculation period can know which rule version was used.

---

# 36. Rule activation

Have:

```text
Status
[ Active ]
```

and:

```text
Effective From
2026-08-22
```

Then:

```text
Aug 21 → Rule v1
Aug 22 → Rule v2
```

This is much more professional for an enterprise tracker.

---

# 37. Recommended database design

You already have:

```text
monitoring_types
monitoring_categories
installed_applications
monitoring_sites
```

Keep those.

Then add something like:

```text
productivity_rule_sets
```

Example:

```text
id
name
description
is_active
effective_from
effective_to
created_at
updated_at
```

Then:

```text
productivity_rule_config
```

or JSON configuration inside the rule set.

For v1, I'd actually prefer a structured configuration table rather than a giant JSON blob if you know the fields won't change often.

---

# 38. Possible rule tables

For example:

```text
productivity_rule_sets
```

```text
id
name
description
is_active
effective_from
effective_to
created_at
updated_at
```

Then:

```text
productivity_rule_settings
```

```text
id
rule_set_id

calculation_method
time_source
unclassified_behavior

productive_weight
neutral_weight
unproductive_weight

idle_excluded
browser_mode

minimum_activity_seconds

created_at
updated_at
```

Then:

```text
productivity_thresholds
```

```text
id
rule_set_id
name
min_percent
max_percent
label
```

Then:

```text
productivity_alert_rules
```

```text
id
rule_set_id
metric
operator
threshold
period
enabled
```

This is clean and extensible.

---

# 39. Don't create unnecessary tables

You don't need:

```text
productive_apps
unproductive_apps
neutral_apps
```

You already have:

```text
monitoring_types
```

Use:

```text
type_id
```

That's the right normalization.

---

# 40. Application classification remains simple

Example:

```text
installed_applications

VS Code
type_id → Productive

Slack
type_id → Productive

Spotify
type_id → Unproductive
```

Website:

```text
monitoring_sites

github.com
type_id → Productive

youtube.com
type_id → Unproductive
```

The Productivity Rule Set never needs to know that GitHub exists.

That is a very important separation.

---

# 41. Rule engine never contains app names

Never do:

```text
if domain == "youtube.com"
```

inside ProductivityEngine.

Never:

```text
if app == "vscode"
```

inside ProductivityEngine.

Those belong in classification configuration.

The engine only sees:

```text
Productive
Neutral
Unproductive
Unclassified
```

This makes the engine reusable.

---

# 42. Example complete flow

Employee opens:

```text
Chrome
```

then:

```text
github.com
```

Tracker records:

```text
app = Chrome
domain = github.com
duration = 45m
foreground = true
```

Classification resolver:

```text
github.com
→ monitoring_sites
→ Productive
```

Productivity engine:

```text
classification = Productive
time = 45m
```

Result:

```text
productiveSeconds += 2700
```

Then employee opens:

```text
youtube.com
```

Tracker:

```text
duration = 20m
```

Resolver:

```text
youtube.com
→ Unproductive
```

Engine:

```text
unproductiveSeconds += 1200
```

No hardcoded knowledge of YouTube exists in the engine.

---

# 43. What the admin experiences

Admin goes:

```text
Configuration
→ Applications
```

sets:

```text
VS Code → Productive
```

Then:

```text
Configuration
→ Websites
```

sets:

```text
YouTube → Unproductive
```

Then:

```text
Configuration
→ Productivity Rules
```

sets:

```text
Calculation:
Productive / Classified

Time:
Foreground

Unclassified:
Exclude

Excellent:
80–100
Good:
60–79
Average:
40–59
Poor:
0–39
```

That's the complete business configuration.

---

# 44. Dashboard should not expose the formula everywhere

Don't show users:

```text
productive / classified * 100
```

Instead:

```text
Productivity
78.6%
```

Then optionally:

```text
Based on 7h classified activity
```

For administrators, provide a details/tooltip:

```text
Productive: 5h 30m
Classified: 7h
Formula: Productive ÷ Classified
```

---

# 45. Employee dashboard

I'd show:

```text
┌──────────────────────────────────────────┐
│ Productivity                             │
│                                          │
│             78.6%                        │
│             Good                         │
│                                          │
│ Productive       5h 30m                  │
│ Neutral             40m                  │
│ Unproductive        50m                  │
│ Unclassified        1h                   │
└──────────────────────────────────────────┘
```

Then:

```text
Focus Score
68.8%
```

Then:

```text
Top Productive Applications
VS Code        3h 10m
GitHub         1h 05m
Slack            35m
```

Then:

```text
Top Unproductive
YouTube          30m
Facebook         20m
```

---

# 46. Productivity trends

Add:

```text
7 Day Productivity
```

Example:

```text
Mon   72%
Tue   81%
Wed   78%
Thu   84%
Fri   76%
```

And another:

```text
Productive Time
```

This lets managers see trends instead of judging one number.

---

# 47. Productivity should be explainable

This is extremely important for your tracker.

If dashboard says:

```text
Productivity = 62%
```

the admin should be able to click it and see:

```text
Why?

Productive       4h 20m
Neutral             50m
Unproductive      1h 30m

Classified        6h 40m

4h20 / 6h40 = 65%
```

And then drill down:

```text
VS Code          3h
GitHub           1h
YouTube          1h
Facebook         30m
```

This makes the metric defensible.

---

# 48. Don't call it "employee performance"

Be careful with terminology.

The system measures:

```text
Computer Activity Productivity
```

It does **not** directly measure:

```text
Actual human productivity
```

Someone could spend 8 hours in VS Code and produce nothing.

Someone could spend 2 hours and complete excellent work.

Your product should therefore say:

```text
Productivity Score
Activity Productivity
Focus Score
```

rather than claiming:

```text
Employee Performance = 82%
```

That's a much more accurate product design.

---

# 49. Recommended V1

Don't build everything immediately.

### V1 Productivity Rules

Build only:

```text
1. Calculation method
2. Time source
3. Unclassified handling
4. Browser overlap handling
5. Performance thresholds
6. Enable/disable Focus Score
```

Then build:

```text
7. Unproductive alerts
8. Minimum duration
9. Custom weights
10. Rule versioning
```

later.

---

# 50. V1 database

I'd start with:

```text
productivity_rule_sets
```

and:

```text
productivity_rule_settings
```

plus:

```text
productivity_thresholds
```

That's enough.

---

# 51. V1 API

Something like:

```http
GET    /api/v1/productivity/rules
POST   /api/v1/productivity/rules
PATCH  /api/v1/productivity/rules/{id}
DELETE /api/v1/productivity/rules/{id}
POST   /api/v1/productivity/rules/{id}/activate
```

And reporting:

```http
GET /api/v1/productivity/employees/{id}
GET /api/v1/productivity/employees/{id}/trend
GET /api/v1/productivity/departments/{id}
GET /api/v1/productivity/organization
```

---

# 52. Server architecture

Given your existing Go server architecture, I'd keep the responsibility separated:

```text
server/
│
├── monitoring/
│   ├── monitoring_handler.go
│   ├── monitoring_service.go
│   └── monitoring_repo.go
│
└── productivity/
    ├── productivity_handler.go
    ├── productivity_service.go
    ├── productivity_repo.go
    ├── productivity_engine.go
    ├── productivity_rules.go
    └── productivity_models.go
```

That follows the architecture you already established for Monitoring.

---

# 53. Frontend architecture

Something like:

```text
configuration/
│
├── applications/
├── websites/
├── categories/
└── productivity-rules/
```

And:

```text
ProductivityRulesPage
   ↓
ProductivityRulesForm
   ↓
CalculationSettings
TimeSettings
ThresholdSettings
AlertSettings
```

---

# 54. Permissions

Create a separate permission:

```text
configuration/productivity-rules
```

Possible permissions:

```text
view
create
update
delete
```

Your current permission architecture already uses Configuration modules for Applications/Websites/Categories, so this should follow the same pattern.

---

# 55. Important security rule

Only authorized admins should modify Productivity Rules.

Why?

Changing:

```text
Unclassified → Neutral
```

could change company-wide productivity reports.

Changing:

```text
Calculation Method
```

could change every employee's score.

So rule changes are **high-impact configuration changes**.

---

# 56. Audit log

Eventually record:

```text
Who changed rules?
What changed?
When?
Old value?
New value?
```

Example:

```text
Amir
Aug 22 2026 15:20

Changed:
Unclassified Activity

From:
Neutral

To:
Exclude
```

This is very useful for enterprise reporting.

---

# 57. The complete architecture

Your final system becomes:

```text
                    CONFIGURATION
                         │
        ┌────────────────┼────────────────┐
        │                │                │
 Applications        Websites        Categories
        │                │
        └────────────────┘
                 │
                 ▼
        Productive / Neutral /
           Unproductive
                 │
                 │
                 ▼
          ACTIVITY COLLECTOR
                 │
        ┌────────┴─────────┐
        │                  │
   App Sessions       Web Activity
        │                  │
        └────────┬─────────┘
                 │
                 ▼
        CLASSIFICATION ENGINE
                 │
                 ▼
        PRODUCTIVITY RULES
                 │
        ┌────────┼───────────┐
        │        │           │
      Time     Weights    Thresholds
        │        │           │
        └────────┼───────────┘
                 ▼
        PRODUCTIVITY ENGINE
                 │
       ┌─────────┼─────────┐
       │         │         │
   Productive Neutral Unproductive
       │         │         │
       └─────────┼─────────┘
                 ▼
              METRICS
                 │
       ┌─────────┼─────────┐
       │         │         │
 Productivity  Focus     Alerts
       │         │         │
       └─────────┼─────────┘
                 ▼
              REPORTS
                 │
       ┌─────────┼─────────┐
       │         │         │
    Employee   Team     Organization
```

---

# 58. The single most important principle

Keep these three things completely separate:

### 1. Classification

```text
"What is this?"
```

Example:

```text
YouTube → Unproductive
```

### 2. Rules

```text
"How should we calculate?"
```

Example:

```text
Unclassified → Exclude
Foreground → Use
Productive / Classified → Percentage
```

### 3. Activity

```text
"What actually happened?"
```

Example:

```text
Employee watched YouTube for 35 minutes.
```

Then:

```text
Classification
       ↓
Unproductive

Activity
       ↓
35 minutes

Rules
       ↓
Include in classified time

Result
       ↓
Unproductive = 35 minutes
```

**That separation is what will make your tracker scalable.**

Your existing system already has the activity/session/web infrastructure and dynamic application/website classification needed for this architecture, so I would **add Productivity Rules and the Productivity Engine rather than redesigning the existing monitoring system**.
