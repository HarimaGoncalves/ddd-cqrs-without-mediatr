// ============================================================================================
//  DDD + CQRS without MediatR  ·  single file  ·  `dotnet run` or DotNetFiddle (.NET 6+ compiler)
//
//  Shop.Domain          Order aggregate, value objects, domain events, repository port
//  Shop.Application     write side: commands + handlers · read side: queries + handlers + projection
//  Shop.Infrastructure  in-memory write store, read store, domain-event dispatcher
//  Shop.Api             composition root + a scripted "HTTP session"
//
//  What replaces MediatR:
//    IRequest / IRequestHandler  ->  ICommandHandler<TCommand>, IQueryHandler<TQuery, TResult>
//    IMediator.Send(...)         ->  inject the handler you need straight into the endpoint
//    IPipelineBehavior           ->  a decorator (LoggingCommandHandler<TCommand>)
//    INotification / Publish     ->  IDomainEventHandler<TEvent> + DomainEventDispatcher
// ============================================================================================
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    // ---------- repository port: the write side only ever needs load + save ----------
    public interface IOrderRepository
    {
        Task<Order?> GetByIdAsync(OrderId id, CancellationToken ct);
        Task SaveAsync(Order order, CancellationToken ct);
    }
}

namespace Shop.Application
{
    using Shop.Domain;

    // =====================================================================================
    //  The whole MediatR replacement: a few tiny interfaces.
    // =====================================================================================
    public interface ICommand { }
    public interface IQuery<TResult> { }

    public interface ICommandHandler<in TCommand> where TCommand : ICommand
    {
        Task HandleAsync(TCommand command, CancellationToken ct);
    }

    public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
    {
        Task<TResult> HandleAsync(TQuery query, CancellationToken ct);
    }

    public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
    {
        Task HandleAsync(TEvent domainEvent, CancellationToken ct);
    }

    // =====================================================================================
    //  WRITE SIDE: commands go through the aggregate and return nothing.
    // =====================================================================================
    public sealed record CreateOrder(Guid OrderId, string CustomerId, string Currency) : ICommand;
    public sealed record AddOrderLine(Guid OrderId, string Sku, int Quantity, decimal UnitPrice) : ICommand;
    public sealed record SubmitOrder(Guid OrderId) : ICommand;

    public sealed class CreateOrderHandler : ICommandHandler<CreateOrder>
    {
        private readonly IOrderRepository _orders;
        public CreateOrderHandler(IOrderRepository orders) => _orders = orders;

        public Task HandleAsync(CreateOrder command, CancellationToken ct) =>
            _orders.SaveAsync(Order.Create(new OrderId(command.OrderId), new CustomerId(command.CustomerId), command.Currency), ct);
    }

    public sealed class AddOrderLineHandler : ICommandHandler<AddOrderLine>
    {
        private readonly IOrderRepository _orders;
        public AddOrderLineHandler(IOrderRepository orders) => _orders = orders;

        public async Task HandleAsync(AddOrderLine command, CancellationToken ct)
        {
            var order = await _orders.GetByIdAsync(new OrderId(command.OrderId), ct)
                        ?? throw new KeyNotFoundException($"Order {command.OrderId} not found.");
            order.AddLine(command.Sku, command.Quantity, new Money(command.UnitPrice, order.Currency));
            await _orders.SaveAsync(order, ct);
        }
    }

    public sealed class SubmitOrderHandler : ICommandHandler<SubmitOrder>
    {
        private readonly IOrderRepository _orders;
        public SubmitOrderHandler(IOrderRepository orders) => _orders = orders;

        public async Task HandleAsync(SubmitOrder command, CancellationToken ct)
        {
            var order = await _orders.GetByIdAsync(new OrderId(command.OrderId), ct)
                        ?? throw new KeyNotFoundException($"Order {command.OrderId} not found.");
            order.Submit(DateTime.UtcNow);
            await _orders.SaveAsync(order, ct);
        }
    }

    // Replaces a MediatR IPipelineBehavior. Written once, wraps every command handler.
    // Validation, transactions, retries, auditing... are just more decorators like this one.
    public sealed class LoggingCommandHandler<TCommand> : ICommandHandler<TCommand> where TCommand : ICommand
    {
        private readonly ICommandHandler<TCommand> _inner;
        public LoggingCommandHandler(ICommandHandler<TCommand> inner) => _inner = inner;

        public async Task HandleAsync(TCommand command, CancellationToken ct)
        {
            var name = typeof(TCommand).Name;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await _inner.HandleAsync(command, ct);
                Console.WriteLine(FormattableString.Invariant($"   [cmd] {name} ok ({stopwatch.Elapsed.TotalMilliseconds:0.00} ms)"));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   [cmd] {name} rejected ({ex.GetType().Name})");
                throw;
            }
        }
    }

    // =====================================================================================
    //  READ SIDE: queries never touch the aggregate or IOrderRepository.
    //  They read a denormalized model shaped for the screen.
    // =====================================================================================
    public sealed record OrderSummary(Guid OrderId, string CustomerId, string Status, int ItemCount, decimal Total, string Currency);

    public interface IOrderSummaryStore // in real life: a SQL view + Dapper, a read replica, Redis, Elasticsearch...
    {
        Task<OrderSummary?> FindAsync(Guid orderId, CancellationToken ct);
        Task<IReadOnlyList<OrderSummary>> ByCustomerAsync(string customerId, CancellationToken ct);
        Task UpsertAsync(OrderSummary summary, CancellationToken ct);
    }

    public sealed record GetOrderSummary(Guid OrderId) : IQuery<OrderSummary?>;
    public sealed record GetCustomerOrders(string CustomerId) : IQuery<IReadOnlyList<OrderSummary>>;

    public sealed class GetOrderSummaryHandler : IQueryHandler<GetOrderSummary, OrderSummary?>
    {
        private readonly IOrderSummaryStore _store;
        public GetOrderSummaryHandler(IOrderSummaryStore store) => _store = store;

        public Task<OrderSummary?> HandleAsync(GetOrderSummary query, CancellationToken ct) =>
            _store.FindAsync(query.OrderId, ct);
    }

    public sealed class GetCustomerOrdersHandler : IQueryHandler<GetCustomerOrders, IReadOnlyList<OrderSummary>>
    {
        private readonly IOrderSummaryStore _store;
        public GetCustomerOrdersHandler(IOrderSummaryStore store) => _store = store;

        public Task<IReadOnlyList<OrderSummary>> HandleAsync(GetCustomerOrders query, CancellationToken ct) =>
            _store.ByCustomerAsync(query.CustomerId, ct);
    }

    // Keeps the read model in sync with the write model by reacting to domain events.
    // Here it runs in-process right after the save; in production it usually runs off an
    // outbox / message bus, which makes the read model eventually consistent.
    public sealed class OrderSummaryProjection :
        IDomainEventHandler<OrderCreated>,
        IDomainEventHandler<OrderLineAdded>,
        IDomainEventHandler<OrderSubmitted>
    {
        private readonly IOrderSummaryStore _store;
        public OrderSummaryProjection(IOrderSummaryStore store) => _store = store;

        public Task HandleAsync(OrderCreated e, CancellationToken ct) =>
            _store.UpsertAsync(new OrderSummary(e.OrderId.Value, e.CustomerId.Value, "Draft", 0, 0m, e.Currency), ct);

        public async Task HandleAsync(OrderLineAdded e, CancellationToken ct)
        {
            var row = await FindAsync(e.OrderId, ct);
            await _store.UpsertAsync(row with
            {
                ItemCount = row.ItemCount + e.Quantity,
                Total = row.Total + e.UnitPrice.Amount * e.Quantity
            }, ct);
        }

        public async Task HandleAsync(OrderSubmitted e, CancellationToken ct)
        {
            var row = await FindAsync(e.OrderId, ct);
            await _store.UpsertAsync(row with { Status = "Submitted", Total = e.Total.Amount }, ct);
        }

        private async Task<OrderSummary> FindAsync(OrderId id, CancellationToken ct) =>
            await _store.FindAsync(id.Value, ct) ?? throw new InvalidOperationException($"No summary for order {id}.");
    }

    // A second, independent reaction to the same event (a MediatR "notification handler").
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

    public sealed class InMemoryOrderSummaryStore : IOrderSummaryStore
    {
        private readonly Dictionary<Guid, OrderSummary> _rows = new();

        public Task<OrderSummary?> FindAsync(Guid orderId, CancellationToken ct) =>
            Task.FromResult(_rows.GetValueOrDefault(orderId));

        public Task<IReadOnlyList<OrderSummary>> ByCustomerAsync(string customerId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<OrderSummary>>(_rows.Values.Where(r => r.CustomerId == customerId).ToList());

        public Task UpsertAsync(OrderSummary summary, CancellationToken ct)
        {
            _rows[summary.OrderId] = summary;
            return Task.CompletedTask;
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
            // Composition root: what you'd normally register in DI. With
            // Microsoft.Extensions.DependencyInjection + Scrutor it's roughly:
            //   services.Scan(s => s.FromAssemblyOf<CreateOrderHandler>()
            //       .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<>))).AsImplementedInterfaces().WithScopedLifetime()
            //       .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>))).AsImplementedInterfaces().WithScopedLifetime());
            //   services.Decorate(typeof(ICommandHandler<>), typeof(LoggingCommandHandler<>));
            // ---------------------------------------------------------------------------------
            var readStore = new InMemoryOrderSummaryStore();
            var projection = new OrderSummaryProjection(readStore);
            var events = new DomainEventDispatcher()
                .Subscribe<OrderCreated>(projection)
                .Subscribe<OrderLineAdded>(projection)
                .Subscribe<OrderSubmitted>(projection)
                .Subscribe<OrderSubmitted>(new SendOrderConfirmation());
            var repository = new InMemoryOrderRepository(events);

            var createOrder = Logged(new CreateOrderHandler(repository));
            var addLine = Logged(new AddOrderLineHandler(repository));
            var submitOrder = Logged(new SubmitOrderHandler(repository));
            IQueryHandler<GetOrderSummary, OrderSummary?> getOrder = new GetOrderSummaryHandler(readStore);
            IQueryHandler<GetCustomerOrders, IReadOnlyList<OrderSummary>> getCustomerOrders = new GetCustomerOrdersHandler(readStore);

            // ---------------------------------------------------------------------------------
            // Scripted "HTTP session". In ASP.NET Core each endpoint injects the exact handler
            // it needs, so there is nothing left to mediate:
            //   app.MapPost("/orders/{id}/submit", (Guid id, ICommandHandler<SubmitOrder> handler, CancellationToken ct)
            //       => handler.HandleAsync(new SubmitOrder(id), ct));
            // ---------------------------------------------------------------------------------
            Console.WriteLine("DDD + CQRS, no MediatR");

            var a = Guid.NewGuid();
            Step("POST /orders  alice, EUR");
            await createOrder.HandleAsync(new CreateOrder(a, "alice", "EUR"), ct);
            Console.WriteLine($"   -> 201 Created {Short(a)}");

            Step($"POST /orders/{Short(a)}/lines  BOOK-DDD x1 @ 45.00");
            await addLine.HandleAsync(new AddOrderLine(a, "BOOK-DDD", 1, 45.00m), ct);

            Step($"POST /orders/{Short(a)}/lines  MUG x2 @ 12.50");
            await addLine.HandleAsync(new AddOrderLine(a, "MUG", 2, 12.50m), ct);

            Step($"GET  /orders/{Short(a)}");
            Show(await getOrder.HandleAsync(new GetOrderSummary(a), ct));

            Step($"POST /orders/{Short(a)}/submit");
            await submitOrder.HandleAsync(new SubmitOrder(a), ct);

            Step($"POST /orders/{Short(a)}/lines  MUG x1 @ 12.50  (after submit)");
            try { await addLine.HandleAsync(new AddOrderLine(a, "MUG", 1, 12.50m), ct); }
            catch (DomainException ex) { Console.WriteLine($"   -> 409 Conflict: {ex.Message}"); }

            var b = Guid.NewGuid();
            Step("POST /orders  alice, EUR");
            await createOrder.HandleAsync(new CreateOrder(b, "alice", "EUR"), ct);
            Console.WriteLine($"   -> 201 Created {Short(b)}");

            Step($"POST /orders/{Short(b)}/lines  MUG x1 @ 12.50");
            await addLine.HandleAsync(new AddOrderLine(b, "MUG", 1, 12.50m), ct);

            Step("GET  /customers/alice/orders");
            foreach (var order in await getCustomerOrders.HandleAsync(new GetCustomerOrders("alice"), ct)) Show(order);
        }

        private static ICommandHandler<T> Logged<T>(ICommandHandler<T> handler) where T : ICommand =>
            new LoggingCommandHandler<T>(handler);

        private static void Step(string request) => Console.WriteLine($"\n> {request}");

        private static void Show(OrderSummary? o) => Console.WriteLine(o is null
            ? "   -> 404 Not Found"
            : FormattableString.Invariant($"   -> {Short(o.OrderId)}  {o.Status,-9}  items: {o.ItemCount}  total: {o.Total:0.00} {o.Currency}"));

        private static string Short(Guid id) => id.ToString()[..8];
    }
}
