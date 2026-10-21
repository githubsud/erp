-- The owner can hide the dashboard's "Getting started" list once the shop is set up (and show it again from settings).
ALTER TABLE core.company ADD COLUMN getting_started_hidden_at timestamptz;
