-- Marks the cost-of-sales group so the income statement can show gross profit (tenants may add accounts under it).
UPDATE fin.account SET subtype = 'cost_of_sales' WHERE code = '51' AND NOT is_postable AND subtype IS NULL;
