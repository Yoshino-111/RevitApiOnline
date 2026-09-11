using Autodesk.Revit.UI;

namespace FamilyMEP.Plugin.Infrastructure;

internal sealed class RevitRequestHandler : IExternalEventHandler
{
    private readonly object _sync = new();
    private Action<UIApplication>? _request;
    private Action<Exception?>? _completed;

    public string GetName() => "FamilyMEP Revit API request";

    public bool TrySetRequest(Action<UIApplication> request, Action<Exception?> completed)
    {
        lock (_sync)
        {
            if (_request is not null)
            {
                return false;
            }

            _request = request;
            _completed = completed;
            return true;
        }
    }

    public void Execute(UIApplication app)
    {
        Action<UIApplication>? request;
        Action<Exception?>? completed;
        lock (_sync)
        {
            request = _request;
            completed = _completed;
            _request = null;
            _completed = null;
        }

        Exception? error = null;
        try
        {
            request?.Invoke(app);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        completed?.Invoke(error);
    }
}
