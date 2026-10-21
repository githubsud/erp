-- When a provider's free allowance is used up, it is busy or down, the request goes to its fallback provider (Gemini -> Groq).
-- A private provider never falls back to a shared one (names would leave unfiltered); the gateway enforces this.
ALTER TABLE ai.provider ADD COLUMN fallback_provider varchar(30);

-- Groq's free tier: another company with its own daily allowance, so the assistant keeps answering when Gemini's is used up.
-- Shared (free tiers may keep data), off until the back-office adds a key from console.groq.com.
INSERT INTO ai.provider (id, code, name, kind, base_url, model, vision_model, fallback_models, is_private, is_default, is_enabled, daily_limit)
VALUES (gen_random_uuid(), 'groq', 'Groq (free tier)', 'open_ai_compatible', 'https://api.groq.com/openai/v1/',
        'openai/gpt-oss-120b', 'meta-llama/llama-4-scout-17b-16e-instruct', 'llama-3.3-70b-versatile,qwen/qwen3-32b', false, false, false, 50)
ON CONFLICT (code) DO NOTHING;

UPDATE ai.provider SET fallback_provider = 'groq' WHERE code = 'gemini' AND fallback_provider IS NULL;
