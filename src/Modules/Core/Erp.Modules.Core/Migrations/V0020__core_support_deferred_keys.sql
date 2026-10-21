-- EF does not know the insert order of a request, its first message and files: check the links at commit, like every other table.
ALTER TABLE core.support_ticket ALTER CONSTRAINT support_ticket_tenant_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_ticket ALTER CONSTRAINT support_ticket_assigned_admin_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_ticket ALTER CONSTRAINT support_ticket_duplicate_of_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_message ALTER CONSTRAINT support_message_ticket_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_message ALTER CONSTRAINT support_message_author_admin_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_attachment ALTER CONSTRAINT support_attachment_ticket_id_fkey DEFERRABLE INITIALLY DEFERRED;
ALTER TABLE core.support_attachment ALTER CONSTRAINT support_attachment_message_id_fkey DEFERRABLE INITIALLY DEFERRED;
