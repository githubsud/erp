-- Employee document numbers (QID, passport) are personal identifiers: encrypted like employee.qid_encrypted.
-- Existing plain values are read as they are and encrypted by DocumentNumberEncryption when the server starts.
ALTER TABLE hr.employee_document RENAME COLUMN doc_no TO doc_no_encrypted;
ALTER TABLE hr.employee_document ALTER COLUMN doc_no_encrypted TYPE varchar(300);
