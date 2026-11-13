using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// Builds the site's middleware pipeline as usual and, when the site's services are disposed, disposes the rate limiters
/// its middleware holds (D-665). The rate limiting middleware starts a partitioned limiter whose timer loop runs for the
/// life of the process: nothing disposes it (one site per process in production), and through the middleware's next
/// delegate it kept the whole site alive. Every test left its site in memory (≈2 MB each, gigabytes over the suite) for
/// the garbage collector to walk. Requests run exactly as before: only the disposal changes.
/// </summary>
internal sealed class CollectablePipelineFactory(IServiceProvider services) : IApplicationBuilderFactory, IDisposable
{
    private readonly List<IDisposable> _limiters = [];

    public IApplicationBuilder CreateBuilder(IFeatureCollection serverFeatures) =>
        new Builder(new ApplicationBuilder(services, serverFeatures), this);

    public void Dispose()
    {
        IDisposable[] limiters;
        lock (_limiters)
        {
            limiters = [.. _limiters];
            _limiters.Clear();
        }

        foreach (var limiter in limiters)
        {
            limiter.Dispose();
        }
    }

    /// <summary>
    /// Finds the rate limiters of a built pipeline: each middleware is the target of its request delegate and holds the
    /// next one in a field, so the walk follows delegates and the fields of their targets.
    /// </summary>
    private void Collect(RequestDelegate pipeline)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<Delegate>([pipeline]);
        while (queue.TryDequeue(out var next))
        {
            if (next.Target is not { } target || !seen.Add(target))
            {
                continue;
            }

            for (var type = target.GetType(); type is not null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    switch (field.GetValue(target))
                    {
                        case Delegate inner:
                            queue.Enqueue(inner);
                            break;
                        case PartitionedRateLimiter<HttpContext> limiter:
                            lock (_limiters)
                            {
                                _limiters.Add(limiter);
                            }

                            break;
                    }
                }
            }
        }
    }

    private sealed class Builder(IApplicationBuilder inner, CollectablePipelineFactory owner) : IApplicationBuilder
    {
        public IServiceProvider ApplicationServices
        {
            get => inner.ApplicationServices;
            set => inner.ApplicationServices = value;
        }

        public IFeatureCollection ServerFeatures => inner.ServerFeatures;

        public IDictionary<string, object?> Properties => inner.Properties;

        public RequestDelegate Build()
        {
            var pipeline = inner.Build();
            owner.Collect(pipeline);
            return pipeline;
        }

        public IApplicationBuilder New() => new Builder(inner.New(), owner);

        public IApplicationBuilder Use(Func<RequestDelegate, RequestDelegate> middleware)
        {
            inner.Use(middleware);
            return this;
        }
    }
}
