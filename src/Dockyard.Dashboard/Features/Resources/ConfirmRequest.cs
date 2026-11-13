using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Features.Resources;

sealed record ConfirmRequest(string Message, string ConfirmText, Command Accept, Command Cancel);
