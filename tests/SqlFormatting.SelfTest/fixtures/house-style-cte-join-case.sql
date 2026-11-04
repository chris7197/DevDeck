CREATE OR ALTER VIEW dbo.vwContainerAndPackCountByOrder
AS
WITH FilteredPackLabels AS (
    SELECT
        CAST(CASE WHEN TRIM(pl.FinishedOrderNo) <> '' AND ISNUMERIC(pl.FinishedOrderNo) = 1 THEN pl.FinishedOrderNo ELSE pl.PL_Order END AS INT) AS OrderNumber,
        CASE WHEN pl.PL_DeliverySuffix = 0 THEN 1 ELSE ISNULL(pl.PL_DeliverySuffix, 1) END AS DeliverySuffix,
        pl.PackingNotes
    FROM dbo.GEN_PackLabels pl
),
AggregatedData AS (
    SELECT OrderNumber, DeliverySuffix, COUNT(1) AS PackCount
    FROM FilteredPackLabels
    GROUP BY OrderNumber, DeliverySuffix
)
SELECT tdl.OrderNumber, tdl.DeliveryNoteNumber, ad.PackCount
FROM dbo.TripDetailsLogistics tdl
LEFT OUTER JOIN AggregatedData ad ON tdl.OrderNumber = ad.OrderNumber AND tdl.DeliveryNoteNumber = ad.DeliverySuffix;
