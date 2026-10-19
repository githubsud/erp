-- Modules can be switched off per company (their screens are hidden and their APIs answer 403). Data is kept.
ALTER TABLE core.company ADD COLUMN disabled_modules varchar(200) NOT NULL DEFAULT '';
