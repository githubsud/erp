-- Free tiers limit each model separately (Gemini: 20 requests a day per model): when one is used up or overloaded the next is tried.
ALTER TABLE ai.provider ADD COLUMN fallback_models varchar(500);
UPDATE ai.provider SET fallback_models = 'gemini-3-flash-preview,gemini-3.5-flash-lite,gemini-3.7-flash,gemini-flash-lite-latest'
 WHERE code = 'gemini' AND fallback_models IS NULL;
