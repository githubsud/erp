-- Text printed at the bottom of till, tailoring and workshop receipts (return policy, thanks, social media).
ALTER TABLE core.company ADD COLUMN receipt_footer_en varchar(300), ADD COLUMN receipt_footer_ar varchar(300);
