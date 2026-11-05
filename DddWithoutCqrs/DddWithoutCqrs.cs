// ============================================================================================
//  DDD without CQRS and without MediatR  ·  single file  ·  `dotnet run` or DotNetFiddle (.NET 6+ compiler)
//
//  Same Order domain as DddWithCqrs.cs. What changes lives in Shop.Application:
//    - one application service per aggregate instead of one handler per use case
//    - reads load the aggregate and map it to a DTO: no read model, no projection
//    - so the repository has to grow query methods (IOrderRepository.GetByCustomerAsync)
//    - no generic pipeline: cross-cutting concerns go inline or in an IOrderService decorator
//  Domain events stay: they are a DDD tool, not a CQRS one.
// ============================================================================================
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Shop.Domain
{
    // ---------- building blocks ----------
    public interface IDomainEvent { }

    public abstract class AggregateRoot<TId>
    {
        private readonly List<IDomainEvent> _domainEvents = new();

        protected AggregateRoot(TId id) => Id = id;

        public TId Id { get; }
        public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

        protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
        public void ClearDomainEvents() => _domainEvents.Clear();
    }

    public sealed class DomainException : Exception
    {
        public DomainException(string message) : base(message) { }
    }

    // ---------- value objects ----------
    public readonly record struct OrderId(Guid Value)
    {
        public override string ToString() => Value.ToString()[..8];
    }

    public readonly record struct CustomerId(string Value)
    {
        public override string ToString() => Value;
    }

    public sealed record Money
    {
        public Money(decimal amount, string currency)
        {
            if (amount < 0) throw new DomainException("Amount cannot be negative.");
            if (currency is not { Length: 3 }) throw new DomainException("Currency must be a 3-letter ISO code.");
            Amount = amount;
            Currency = currency.ToUpperInvariant();
        }

        public decimal Amount { get; }
        public string Currency { get; }

        public static Money Zero(string currency) => new(0m, currency);

        public Money Add(Money other) => other.Currency == Currency
            ? new Money(Amount + other.Amount, Currency)
            : throw new DomainException($"Cannot add {other.Currency} to {Currency}.");

        public Money Times(int quantity) => new(Amount * quantity, Currency);

        public override string ToString() => FormattableString.Invariant($"{Amount:0.00} {Currency}");
    }

    // ---------- aggregate ----------
    public enum OrderStatus { Draft, Submitted }

    public sealed class OrderLine // entity inside the aggregate: only Order creates it
    {
        internal OrderLine(string sku, int quantity, Money unitPrice)
        {
            Sku = sku;
            Quantity = quantity;
            UnitPrice = unitPrice;
        }

        public string Sku { get; }
        public int Quantity { get; }
        public Money UnitPrice { get; }
        public Money Total => UnitPrice.Times(Quantity);
    }

    public sealed class Order : AggregateRoot<OrderId>
    {
        private readonly List<OrderLine> _lines = new();

        private Order(OrderId id, CustomerId customerId, string currency) : base(id)
        {
            CustomerId = customerId;
            Currency = currency;
        }

        public CustomerId CustomerId { get; }
        public string Currency { get; }
        public OrderStatus Status { get; private set; } = OrderStatus.Draft;
        public IReadOnlyList<OrderLine> Lines => _lines;
        public Money Total => _lines.Aggregate(Money.Zero(Currency), (sum, line) => sum.Add(line.Total));

        public static Order Create(OrderId id, CustomerId customerId, string currency)
        {
            var order = new Order(id, customerId, Money.Zero(currency).Currency); // Money validates the currency
            order.Raise(new OrderCreated(id, customerId, order.Currency));
            return order;
        }

        public void AddLine(string sku, int quantity, Money unitPrice)
        {
            EnsureDraft();
            if (string.IsNullOrWhiteSpace(sku)) throw new DomainException("SKU is required.");
            if (quantity <= 0) throw new DomainException("Quantity must be positive.");
            if (unitPrice.Currency != Currency) throw new DomainException($"Order is in {Currency}, price is in {unitPrice.Currency}.");

            _lines.Add(new OrderLine(sku, quantity, unitPrice));
            Raise(new OrderLineAdded(Id, sku, quantity, unitPrice));
        }

        public void Submit(DateTime submittedAtUtc)
        {
            EnsureDraft();
            if (_lines.Count == 0) throw new DomainException("Cannot submit an empty order.");

            Status = OrderStatus.Submitted;
            Raise(new OrderSubmitted(Id, CustomerId, Total, submittedAtUtc));
        }

        private void EnsureDraft()
        {
            if (Status != OrderStatus.Draft) throw new DomainException($"Order {Id} is {Status} and can no longer change.");
        }
    }

    // ---------- domain events ----------
    public sealed record OrderCreated(OrderId OrderId, CustomerId CustomerId, string Currency) : IDomainEvent;
    public sealed record OrderLineAdded(OrderId OrderId, string Sku, int Quantity, Money UnitPrice) : IDomainEvent;
    public sealed record OrderSubmitted(OrderId OrderId, CustomerId CustomerId, Money Total, DateTime SubmittedAtUtc) : IDomainEvent;

    // ---------- repository port ----------
    public interface IOrderRepository
    {
        Task<Order?> GetByIdAsync(OrderId id, CancellationToken ct);
        Task SaveAsync(Order order, CancellationToken ct);

        // A read concern living in the write-model repository. Every new screen tends to add
        // another one of these; that pressure is exactly what CQRS relieves.
        Task<IReadOnlyList<Order>> GetByCustomerAsync(CustomerId customerId, CancellationToken ct);
    }
}

namespace Shop.Application
{
    using Shop.Domain;

    // DTOs returned to the API, mapped from the aggregate on every read.
    public sealed record OrderLineDto(string Sku, int Quantity, decimal UnitPrice);

    public sealed record OrderDto(Guid Id, string CustomerId, string Status, IReadOnlyList<OrderLineDto> Lines, decimal Total, string Currency)
    {
        public int ItemCount => Lines.Sum(line => line.Quantity);
    }

    public interface IOrderService
    {
        Task<Guid> CreateAsync(string customerId, string currency, CancellationToken ct);
        Task AddLineAsync(Guid orderId, string sku, int quantity, decimal unitPrice, CancellationToken ct);
        Task SubmitAsync(Guid orderId, CancellationToken ct);
        Task<OrderDto?> GetAsync(Guid orderId, CancellationToken ct);
        Task<IReadOnlyList<OrderDto>> GetByCustomerAsync(string customerId, CancellationToken ct);
    }

    // Classic application service: every use case of the aggregate in one class, and the same
    // domain model serves writes AND reads. Logging, validation and transactions go inline here,
    // or in an IOrderService decorator that has to re-implement every method.
    public sealed class OrderService : IOrderService
    {
        private readonly IOrderRepository _orders;
        public OrderService(IOrderRepository orders) => _orders = orders;

        // ---------- writes: exactly the same domain calls as the CQRS handlers ----------
        public async Task<Guid> CreateAsync(string customerId, string currency, CancellationToken ct)
        {
            var order = Order.Create(new OrderId(Guid.NewGuid()), new CustomerId(customerId), currency);
            await _orders.SaveAsync(order, ct);
            return order.Id.Value;
        }

        public async Task AddLineAsync(Guid orderId, string sku, int quantity, decimal unitPrice, CancellationToken ct)
        {
            var order = await LoadAsync(orderId, ct);
            order.AddLine(sku, quantity, new Money(unitPrice, order.Currency));
            await _orders.SaveAsync(order, ct);
        }

        public async Task SubmitAsync(Guid orderId, CancellationToken ct)
        {
            var order = await LoadAsync(orderId, ct);
            order.Submit(DateTime.UtcNow);
            await _orders.SaveAsync(order, ct);
        }

        // ---------- reads: load whole aggregates, map them to DTOs ----------
        public async Task<OrderDto?> GetAsync(Guid orderId, CancellationToken ct)
        {
            var order = await _orders.GetByIdAsync(new OrderId(orderId), ct);
            return order is null ? null : ToDto(order);
        }

        public async Task<IReadOnlyList<OrderDto>> GetByCustomerAsync(string customerId, CancellationToken ct)
        {
            var orders = await _orders.GetByCustomerAsync(new CustomerId(customerId), ct);
            return orders.Select(ToDto).ToList();
        }

        private async Task<Order> LoadAsync(Guid orderId, CancellationToken ct) =>
            await _orders.GetByIdAsync(new OrderId(orderId), ct) ?? throw new KeyNotFoundException($"Order {orderId} not found.");

        private static OrderDto ToDto(Order order) => new(
            order.Id.Value,
            order.CustomerId.Value,
            order.Status.ToString(),
            order.Lines.Select(line => new OrderLineDto(line.Sku, line.Quantity, line.UnitPrice.Amount)).ToList(),
            order.Total.Amount,
            order.Currency);
    }

    public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
    {
        Task HandleAsync(TEvent domainEvent, CancellationToken ct);
    }

    // Domain events still earn their keep without CQRS: side effects like this one.
    public sealed class SendOrderConfirmation : IDomainEventHandler<OrderSubmitted>
    {
        public Task HandleAsync(OrderSubmitted e, CancellationToken ct)
        {
            Console.WriteLine($"   [event] OrderSubmitted -> confirmation email to {e.CustomerId} ({e.Total})");
            return Task.CompletedTask;
        }
    }
}

namespace Shop.Infrastructure
{
    using Shop.Application;
    using Shop.Domain;

    // Replaces MediatR's IPublisher.Publish for domain events.
    public sealed class DomainEventDispatcher
    {
        private readonly Dictionary<Type, List<Func<IDomainEvent, CancellationToken, Task>>> _handlers = new();

        public DomainEventDispatcher Subscribe<TEvent>(IDomainEventHandler<TEvent> handler) where TEvent : IDomainEvent
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                _handlers[typeof(TEvent)] = list = new();
            }
            list.Add((e, ct) => handler.HandleAsync((TEvent)e, ct));
            return this;
        }

        public async Task DispatchAsync(IEnumerable<IDomainEvent> events, CancellationToken ct)
        {
            foreach (var e in events)
            {
                if (!_handlers.TryGetValue(e.GetType(), out var handlers)) continue;
                foreach (var handle in handlers) await handle(e, ct);
            }
        }
    }

    public sealed class InMemoryOrderRepository : IOrderRepository
    {
        private readonly Dictionary<OrderId, Order> _orders = new();
        private readonly DomainEventDispatcher _events;
        public InMemoryOrderRepository(DomainEventDispatcher events) => _events = events;

        public Task<Order?> GetByIdAsync(OrderId id, CancellationToken ct) =>
            Task.FromResult(_orders.GetValueOrDefault(id));

        public Task<IReadOnlyList<Order>> GetByCustomerAsync(CustomerId customerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.CustomerId == customerId).ToList());

        public async Task SaveAsync(Order order, CancellationToken ct)
        {
            _orders[order.Id] = order;

            // Same idea as overriding DbContext.SaveChangesAsync: persist, then publish what the
            // aggregate raised. In production, write the events to an outbox in the same transaction.
            var raised = order.DomainEvents.ToList();
            order.ClearDomainEvents();
            await _events.DispatchAsync(raised, ct);
        }
    }
}

namespace Shop.Api
{
    using Shop.Application;
    using Shop.Domain;
    using Shop.Infrastructure;

    public class Program
    {
        public static async Task Main()
        {
            var ct = CancellationToken.None;

            // ---------------------------------------------------------------------------------
            // Composition root. In DI this is one line: services.AddScoped<IOrderService, OrderService>();
            // ---------------------------------------------------------------------------------
            var events = new DomainEventDispatcher().Subscribe<OrderSubmitted>(new SendOrderConfirmation());
            IOrderService orders = new OrderService(new InMemoryOrderRepository(events));

            // ---------------------------------------------------------------------------------
            // Scripted "HTTP session". Every endpoint injects the same service, so again there is
            // nothing to mediate:
            //   app.MapPost("/orders/{id}/submit", (Guid id, IOrderService orders, CancellationToken ct)
            //       => orders.SubmitAsync(id, ct));
            // ---------------------------------------------------------------------------------
            Console.WriteLine("DDD without CQRS, no MediatR");

            Step("POST /orders  alice, EUR");
            var a = await orders.CreateAsync("alice", "EUR", ct);
            Console.WriteLine($"   -> 201 Created {Short(a)}");

            Step($"POST /orders/{Short(a)}/lines  BOOK-DDD x1 @ 45.00");
            await orders.AddLineAsync(a, "BOOK-DDD", 1, 45.00m, ct);

            Step($"POST /orders/{Short(a)}/lines  MUG x2 @ 12.50");
            await orders.AddLineAsync(a, "MUG", 2, 12.50m, ct);

            Step($"GET  /orders/{Short(a)}");
            Show(await orders.GetAsync(a, ct));

            Step($"POST /orders/{Short(a)}/submit");
            await orders.SubmitAsync(a, ct);

            Step($"POST /orders/{Short(a)}/lines  MUG x1 @ 12.50  (after submit)");
            try { await orders.AddLineAsync(a, "MUG", 1, 12.50m, ct); }
            catch (DomainException ex) { Console.WriteLine($"   -> 409 Conflict: {ex.Message}"); }

            Step("POST /orders  alice, EUR");
            var b = await orders.CreateAsync("alice", "EUR", ct);
            Console.WriteLine($"   -> 201 Created {Short(b)}");

            Step($"POST /orders/{Short(b)}/lines  MUG x1 @ 12.50");
            await orders.AddLineAsync(b, "MUG", 1, 12.50m, ct);

            Step("GET  /customers/alice/orders");
            foreach (var order in await orders.GetByCustomerAsync("alice", ct)) Show(order);
        }

        private static void Step(string request) => Console.WriteLine($"\n> {request}");

        private static void Show(OrderDto? o) => Console.WriteLine(o is null
            ? "   -> 404 Not Found"
            : FormattableString.Invariant($"   -> {Short(o.Id)}  {o.Status,-9}  items: {o.ItemCount}  total: {o.Total:0.00} {o.Currency}"));

        private static string Short(Guid id) => id.ToString()[..8];
    }
}
