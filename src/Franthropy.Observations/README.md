# Franthropy.Observations

Versioned, product-neutral observation contracts and durable shared storage for Franthropy consumers.

The package records only evidence the game client actually exposed. Missing, transitioning, partial, or owner-mismatched observations never become authoritative empty state.

Readers open without creating or migrating the database. The elected writer performs forward-only migrations under an exclusive lock after making a SQLite-consistent backup, and committed revisions wake other loaded copies through an event-driven change signal rather than a timer.
