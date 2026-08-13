using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace RapidsolDestek.Web.Navigation;

/// <summary>Marks a controller or action with the sidebar/header nav key (mockup body[data-nav] equivalent).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class NavKeyAttribute(string key) : Attribute, IResultFilter
{
    public string Key { get; } = key;

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Controller is Controller c)
            c.ViewData["NavKey"] = Key;
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
