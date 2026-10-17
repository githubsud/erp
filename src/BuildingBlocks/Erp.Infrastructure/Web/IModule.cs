using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Infrastructure.Web;

/// <summary>A business module plugged into the host. Its assembly also supplies EF configurations and SQL migrations.</summary>
public interface IModule
{
    string Name { get; }
    Assembly Assembly => GetType().Assembly;
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder app);
}
