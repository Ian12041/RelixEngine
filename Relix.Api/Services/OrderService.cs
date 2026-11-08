using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Relix.Api.DTOs;

namespace Relix.Api.Services;

public class OrderService
{
    private readonly string _connectionString;

    public OrderService(IConfiguration configuration)
    {
        // 從 appsettings.json 讀取資料庫連線字串
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("找不到 DefaultConnection 連線字串設定");
    }

    /// <summary>
    /// 建立訂單並同步寫入 Outbox 事件 (Transactional Outbox Pattern 核心)
    /// </summary>
    public async Task<OrderResponse> CreateOrderAsync(CreateOrderRequest request)
    {
        // 產生唯一的業務訂單編號 (例如: ORD-20261108-A1B2C3D4)
        var orderNo = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}";
        var now = DateTime.UtcNow;

        // 1. 建立並開啟資料庫連線
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // 2. 開啟本地資料庫交易 (Local Transaction)
        // 重點：接下來的兩個 INSERT 必須「同生共死」，任何一個失敗就全部取消！
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            // 【動作 A】：寫入業務訂單主表 (Orders)
            const string insertOrderSql = @"
                INSERT INTO Orders (OrderNo, CustomerName, Amount, Status, CreatedAt, UpdatedAt)
                OUTPUT INSERTED.Id
                VALUES (@OrderNo, @CustomerName, @Amount, 'CREATED', @Now, @Now);";

            var orderId = await connection.ExecuteScalarAsync<long>(
                insertOrderSql,
                new
                {
                    OrderNo = orderNo,
                    request.CustomerName,
                    request.Amount,
                    Now = now
                },
                transaction: transaction // ★ 關鍵：必須綁定在同一個 transaction 內！
            );

            // 組裝要發送給外部系統/訊息佇列的事件內容 (Payload)
            var eventId = Guid.NewGuid();
            var eventPayload = JsonSerializer.Serialize(new
            {
                EventId = eventId,
                OrderId = orderId,
                OrderNo = orderNo,
                CustomerName = request.CustomerName,
                Amount = request.Amount,
                OccurredAt = now
            });

            // 【動作 B】：寫入外發箱表 (OutboxMessages)
            const string insertOutboxSql = @"
                INSERT INTO OutboxMessages (EventId, EventType, Payload, Status, RetryCount, CreatedAt)
                VALUES (@EventId, @EventType, @Payload, 'PENDING', 0, @Now);";

            await connection.ExecuteAsync(
                insertOutboxSql,
                new
                {
                    EventId = eventId,
                    EventType = "OrderCreated",
                    Payload = eventPayload,
                    Now = now
                },
                transaction: transaction // ★ 關鍵：與訂單綁定在同一個 transaction 內！
            );

            // 3. 兩個寫入都成功了，正式提交交易 (Commit)
            await transaction.CommitAsync();

            return new OrderResponse
            {
                OrderId = orderId,
                OrderNo = orderNo,
                CustomerName = request.CustomerName,
                Amount = request.Amount,
                Status = "CREATED",
                CreatedAt = now
            };
        }
        catch
        {
            // 4. 只要中間任何一行出錯 (例如寫 Outbox 時斷線)，立刻全部回滾 (Rollback)
            // 確保絕對不會發生「訂單成立了，但通知訊息沒寫進去」的雙寫不一致！
            await transaction.RollbackAsync();
            throw;
        }
    }
}