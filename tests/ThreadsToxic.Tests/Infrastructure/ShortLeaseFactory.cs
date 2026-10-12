namespace ThreadsToxic.Tests.Infrastructure;

/// <summary>Lease 1 giây, quét mỗi 1 giây: test hết hạn không cần chờ 45 giây.</summary>
public class ShortLeaseFactory : AppFactory
{
    public ShortLeaseFactory() : base(leaseSeconds: 1, sweepSeconds: 1)
    {
    }
}
