-- "gemini-flash-latest" is often overloaded on the free tier (slow or 503); gemini-3.5-flash answers in seconds and reads images.
UPDATE ai.provider SET model = 'gemini-3.5-flash', vision_model = 'gemini-3.5-flash', updated_at = now()
 WHERE code = 'gemini' AND model = 'gemini-flash-latest';
