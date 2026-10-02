using FluentAssertions;
using Highway.Abstractions;
using Highway.Client.Scanning;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Highway.Client.Tests.Scanning;

public class DeadLetterScannerTests
{
    private readonly DefaultTypeScanner _scanner = new();

    [Queue("orders.placed")]
    public sealed class OrderPlacedMessage : ISend
    {
        public string? OrderId { get; set; }
    }

    [Service("payments.charge")]
    public sealed class ChargePaymentRequest : IReturn<ChargePaymentResponse>
    {
        public decimal Amount { get; set; }
    }

    public sealed class ChargePaymentResponse : Output
    {
        public string? TransactionId { get; set; }
    }

    [Channel("shipments.dispatched")]
    public sealed class ShipmentDispatchedEvent : IPublish
    {
        public string? TrackingNumber { get; set; }
    }

    public sealed class UnknownContract
    {
        public string? Data { get; set; }
    }

    public sealed class OrderDeadLetterHandler : IDeadLetter<OrderPlacedMessage>
    {
        public Task<DeadLetterAction> HandleAsync(DeadLetter<OrderPlacedMessage> letter, CancellationToken ct = default)
            => Task.FromResult(DeadLetterAction.Keep);
    }

    public sealed class OrderDeadLetterHandlerDuplicate : IDeadLetter<OrderPlacedMessage>
    {
        public Task<DeadLetterAction> HandleAsync(DeadLetter<OrderPlacedMessage> letter, CancellationToken ct = default)
            => Task.FromResult(DeadLetterAction.Discard);
    }

    [ServiceLifetime(HighwayServiceLifetime.Singleton)]
    public sealed class PaymentDeadLetterHandler : IDeadLetter<ChargePaymentRequest>
    {
        public Task<DeadLetterAction> HandleAsync(DeadLetter<ChargePaymentRequest> letter, CancellationToken ct = default)
            => Task.FromResult(DeadLetterAction.Discard);
    }

    public sealed class ShipmentDeadLetterHandler : IDeadLetter<ShipmentDispatchedEvent>
    {
        public Task<DeadLetterAction> HandleAsync(DeadLetter<ShipmentDispatchedEvent> letter, CancellationToken ct = default)
            => Task.FromResult(DeadLetterAction.Requeue(TimeSpan.FromSeconds(30)));
    }

    public sealed class UnknownDeadLetterHandler : IDeadLetter<UnknownContract>
    {
        public Task<DeadLetterAction> HandleAsync(DeadLetter<UnknownContract> letter, CancellationToken ct = default)
            => Task.FromResult(DeadLetterAction.Keep);
    }

    [Fact]
    public void Scan_DiscoversQueueDeadLetterHandler()
    {
        var types = new List<Type> { typeof(OrderPlacedMessage), typeof(OrderDeadLetterHandler) };
        var result = _scanner.ScanTypes(types);

        result.DeadLetters.Should().ContainSingle();
        var descriptor = result.DeadLetters[0];
        descriptor.TargetKind.Should().Be(DeadLetterTargetKind.Queue);
        descriptor.TargetName.Should().Be("orders.placed");
        descriptor.MessageType.Should().Be(typeof(OrderPlacedMessage));
        descriptor.HandlerType.Should().Be(typeof(OrderDeadLetterHandler));
        descriptor.Lifetime.Should().Be(HighwayServiceLifetime.Scoped);
    }

    [Fact]
    public void Scan_DiscoversServiceDeadLetterHandler_WithCustomLifetime()
    {
        var types = new List<Type> { typeof(ChargePaymentRequest), typeof(PaymentDeadLetterHandler) };
        var result = _scanner.ScanTypes(types);

        result.DeadLetters.Should().ContainSingle();
        var descriptor = result.DeadLetters[0];
        descriptor.TargetKind.Should().Be(DeadLetterTargetKind.Service);
        descriptor.TargetName.Should().Be("payments.charge");
        descriptor.MessageType.Should().Be(typeof(ChargePaymentRequest));
        descriptor.HandlerType.Should().Be(typeof(PaymentDeadLetterHandler));
        descriptor.Lifetime.Should().Be(HighwayServiceLifetime.Singleton);
    }

    [Fact]
    public void Scan_DiscoversGroupDeadLetterHandler()
    {
        var types = new List<Type> { typeof(ShipmentDispatchedEvent), typeof(ShipmentDeadLetterHandler) };
        var result = _scanner.ScanTypes(types);

        result.DeadLetters.Should().ContainSingle();
        var descriptor = result.DeadLetters[0];
        descriptor.TargetKind.Should().Be(DeadLetterTargetKind.Group);
        descriptor.TargetName.Should().Be("shipments.dispatched");
        descriptor.MessageType.Should().Be(typeof(ShipmentDispatchedEvent));
        descriptor.HandlerType.Should().Be(typeof(ShipmentDeadLetterHandler));
    }

    [Fact]
    public void Scan_DuplicateDeadLetterHandler_ThrowsDuplicateDeadLetterHandlerException()
    {
        var types = new List<Type>
        {
            typeof(OrderPlacedMessage),
            typeof(OrderDeadLetterHandler),
            typeof(OrderDeadLetterHandlerDuplicate)
        };

        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<DuplicateDeadLetterHandlerException>()
            .WithMessage($"*'{typeof(OrderDeadLetterHandler).FullName}'*'{typeof(OrderDeadLetterHandlerDuplicate).FullName}'*'{typeof(OrderPlacedMessage).FullName}'*");
    }

    [Fact]
    public void Scan_UnknownContract_ThrowsUnknownDeadLetterContractException()
    {
        var types = new List<Type> { typeof(UnknownContract), typeof(UnknownDeadLetterHandler) };

        var act = () => _scanner.ScanTypes(types);

        act.Should().Throw<UnknownDeadLetterContractException>()
            .WithMessage($"*'{typeof(UnknownDeadLetterHandler).FullName}'*'{typeof(UnknownContract).FullName}'*");
    }

    [Fact]
    public void CatalogDiRegistrar_RegistersDeadLetterHandlers()
    {
        var types = new List<Type>
        {
            typeof(OrderPlacedMessage), typeof(OrderDeadLetterHandler),
            typeof(ChargePaymentRequest), typeof(PaymentDeadLetterHandler)
        };
        var result = _scanner.ScanTypes(types);

        var services = new ServiceCollection();
        CatalogDiRegistrar.Register(services, result);

        services.Should().Contain(d => d.ServiceType == typeof(OrderDeadLetterHandler) && d.Lifetime == ServiceLifetime.Scoped);
        services.Should().Contain(d => d.ServiceType == typeof(PaymentDeadLetterHandler) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public async Task ExpressionDelegateCompiler_CompilesDeadLetterDelegate()
    {
        var compiler = new ExpressionDelegateCompiler();
        var del = compiler.CompileDeadLetterDelegate(typeof(OrderDeadLetterHandler), typeof(OrderPlacedMessage));

        var handler = new OrderDeadLetterHandler();
        var letter = new DeadLetter<OrderPlacedMessage>(
            Message: new OrderPlacedMessage { OrderId = "ord-123" },
            RawPayload: new byte[] { 1, 2, 3 },
            Id: "msg-1",
            DeadLetterId: "000000010000000000000001",
            Target: new DeadLetterTarget(DeadLetterTargetKind.Queue, "orders.placed"),
            Attempts: 3,
            Requeues: 0,
            DeadLetteredAt: DateTimeOffset.UtcNow,
            Reason: "MAX_ATTEMPTS",
            LastFailure: null,
            FirstFailure: null);

        var action = await del(handler, letter, CancellationToken.None);
        action.Should().Be(DeadLetterAction.Keep);
    }
}
