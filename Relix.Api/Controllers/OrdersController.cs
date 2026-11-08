using Microsoft.AspNetCore.Mvc;
using Relix.Api.DTOs;
using Relix.Api.Services;

namespace Relix.Api.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orderService;

    public OrdersController(OrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequest request)
    {
        var result = await _orderService.CreateOrderAsync(request);
        // 回傳標準 RESTful 201 Created 狀態碼
        return Created($"/api/v1/orders/{result.OrderNo}", result);
    }
}