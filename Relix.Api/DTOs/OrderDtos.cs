using System.ComponentModel.DataAnnotations;

namespace Relix.Api.DTOs;

// 前端建立訂單時傳入的資料格式
public class CreateOrderRequest
{
    [Required(ErrorMessage = "客戶名稱為必填")]
    [MaxLength(100)]
    public string CustomerName { get; set; } = string.Empty;

    [Range(1, 10000000, ErrorMessage = "金額必須大於 0")]
    public decimal Amount { get; set; }
}

// API 回傳給前端的標準格式
public class OrderResponse
{
    public long OrderId { get; set; }
    public string OrderNo { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}