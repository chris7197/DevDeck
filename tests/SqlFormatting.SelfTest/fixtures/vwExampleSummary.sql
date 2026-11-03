CREATE OR ALTER VIEW [dbo].[vwExampleSummary]
AS
WITH RecentItems AS (
    SELECT i.ID, i.CategoryID, i.Name, ROW_NUMBER() OVER (PARTITION BY i.CategoryID ORDER BY i.ID DESC) AS rn
    FROM dbo.Items AS i
    WHERE i.IsActive = 1
), Totals AS (
    SELECT t.CategoryID, SUM(t.Amount) AS TotalAmount, COUNT(*) AS LineCount
    FROM dbo.ItemTransactions AS t
    GROUP BY t.CategoryID
)
SELECT r.ID, r.Name, c.Description, ISNULL(t.TotalAmount, 0) AS TotalAmount
FROM RecentItems r
LEFT JOIN dbo.Categories c ON c.ID = r.CategoryID
LEFT JOIN Totals t ON t.CategoryID = r.CategoryID
WHERE r.rn = 1; -- Retain this comment when formatting
