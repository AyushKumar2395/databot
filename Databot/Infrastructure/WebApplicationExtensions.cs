using System.Reflection;

namespace Databot.Infrastructure;

public static class WebApplicationExtensions
{
    extension(WebApplication app)
    {
        RouteGroupBuilder MapGroup(EndpointGroupBase instance)
        {
            var groupName = instance.GroupName ?? instance.GetType().Name;
            return app.MapGroup($"/api/{groupName.ToLower()}")
                .WithTags(groupName);
        }

        public WebApplication MapEndpoints()
        {
            var assembly = Assembly.GetExecutingAssembly();

            var endpointGroupType = typeof(EndpointGroupBase);

            var endpointGroupTypes = assembly.GetExportedTypes()
                .Where(t => t.IsSubclassOf(endpointGroupType));

            foreach (var type in endpointGroupTypes)
            {
                if (Activator.CreateInstance(type) is not EndpointGroupBase instance) continue;

                instance.Map(app.MapGroup(instance));
            }

            return app;
        }
    }
}