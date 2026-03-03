namespace Application.Common.Interfaces;

public interface IQueryCodeRouterService
{
    /// <summary>
    /// Routes a question to a deterministic QueryCode (template-first pipeline). Return empty when unknown.
    /// This is intentionally conservative: it should never guess a code that could run the wrong template.
    /// </summary>
    string Route(string question, string environmentTag);
}
