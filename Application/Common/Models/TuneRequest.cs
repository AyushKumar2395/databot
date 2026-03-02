using Domain.Enums;

namespace Application.Common.Models;

public record AskQuestionRequest(AgentSwitcher AgentModel, string Question, string Environment, string[] Servers);