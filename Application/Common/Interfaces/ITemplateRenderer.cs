using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Renders ToolRegistry ScriptTemplate by binding parameters from user/tuned text.
/// </summary>
public interface ITemplateRenderer
{
    TemplateRenderResult Render(TemplateRenderRequest request);
    TemplateRenderResult RenderWithBoundParameters(TemplateRenderBoundRequest request);
}
