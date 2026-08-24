-- Monthly active accounts and their spend, with the previous month alongside.
-- Runs on SQL Server 2019 or newer (STRING_AGG, window frames).

DECLARE @Since date = DATEFROMPARTS(YEAR(GETDATE()) - 1, 1, 1);
DECLARE @MinOrders int = 3;

WITH monthly AS (
    SELECT
        a.AccountId,
        a.DisplayName,
        DATEFROMPARTS(YEAR(o.PlacedAt), MONTH(o.PlacedAt), 1) AS Period,
        COUNT(*)                                             AS Orders,
        SUM(o.TotalAmount)                                   AS Revenue,
        STRING_AGG(o.Channel, ', ')                          AS Channels
    FROM dbo.Orders      AS o
    JOIN dbo.Accounts    AS a ON a.AccountId = o.AccountId
    LEFT JOIN dbo.Refunds AS r ON r.OrderId = o.OrderId
    WHERE o.PlacedAt >= @Since
      AND o.Status IN ('shipped', 'delivered')
      AND r.RefundId IS NULL
    GROUP BY a.AccountId, a.DisplayName,
             DATEFROMPARTS(YEAR(o.PlacedAt), MONTH(o.PlacedAt), 1)
    HAVING COUNT(*) >= @MinOrders
)
SELECT
    m.Period,
    m.DisplayName,
    m.Orders,
    CAST(m.Revenue AS decimal(12, 2))                        AS Revenue,
    LAG(m.Revenue) OVER (PARTITION BY m.AccountId
                         ORDER BY m.Period)                  AS PriorRevenue,
    ROUND(100.0 * (m.Revenue - LAG(m.Revenue) OVER (PARTITION BY m.AccountId
                                                    ORDER BY m.Period))
          / NULLIF(LAG(m.Revenue) OVER (PARTITION BY m.AccountId
                                        ORDER BY m.Period), 0), 1) AS PctChange,
    CASE
        WHEN m.Revenue > 10000 THEN 'key'
        WHEN m.Orders  > 25    THEN 'frequent'
        ELSE 'standard'
    END                                                      AS Segment,
    m.Channels
FROM monthly AS m
ORDER BY m.Period DESC, Revenue DESC
OFFSET 0 ROWS FETCH NEXT 100 ROWS ONLY;
