using System.Text;
using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;
using Microsoft.Playwright;

namespace Computerwelt.Playwright.Api;

/// <summary>A <c>Response</c>: what the browser got back, as the script can read it.</summary>
internal sealed class PyResponse : PlaywrightObject
{
    private readonly IResponse _response;

    public PyResponse(Bridge bridge, IResponse response) : base(bridge) => _response = response;

    public override string TypeName => "Response";

    public override string Repr() => $"<Response status={_response.Status} url={_response.Url}>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        // Properties upstream, so values here.
        "url" => new PyStr(_response.Url),
        "ok" => PyBool.Of(_response.Ok),
        "status" => PyInt.From(_response.Status),
        "status_text" => new PyStr(_response.StatusText),
        "headers" => Values.FromMapping(_response.Headers),
        "from_service_worker" => PyBool.Of(_response.FromServiceWorker),
        "request" => new PyRequest(Bridge, _response.Request),

        "text" => Method("text", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _response.TextAsync()));
        }),

        "body" => Method("body", arguments =>
        {
            arguments.Done(0);

            var bytes = Bridge.Block(() => _response.BodyAsync());

            if (bytes.Length > Bridge.Options.MaxTransferBytes)
            {
                throw Errors.Fail(
                    $"the response body is {bytes.Length} bytes, over "
                    + $"PlaywrightOptions.MaxTransferBytes ({Bridge.Options.MaxTransferBytes}).");
            }

            return new PyBytes(bytes);
        }),

        "json" => Method("json", arguments =>
        {
            arguments.Done(0);

            // Decoded here rather than by the driver, so a body that is not JSON raises the
            // error a script catches with `except JSONDecodeError` and not a driver failure.
            var text = Bridge.Block(() => _response.TextAsync());
            return Values.FromJsonText(text, arguments.Name);
        }),

        "finished" => Action("finished", arguments =>
        {
            arguments.Done(0);
            Bridge.Block(() => _response.FinishedAsync());
        }),

        "all_headers" => Method("all_headers", arguments =>
        {
            arguments.Done(0);
            return Values.FromMapping(Bridge.Block(() => _response.AllHeadersAsync()));
        }),

        "header_value" => Method("header_value", arguments =>
        {
            var header = arguments.String(0, "name");
            arguments.Done(1);

            return Values.FromOptional(Bridge.Block(() => _response.HeaderValueAsync(header)));
        }),

        "header_values" => Method("header_values", arguments =>
        {
            var header = arguments.String(0, "name");
            arguments.Done(1);

            return Values.FromStrings(Bridge.Block(() => _response.HeaderValuesAsync(header)));
        }),

        "http_version" => Method("http_version", arguments =>
        {
            arguments.Done(0);
            return new PyStr(Bridge.Block(() => _response.HttpVersionAsync()));
        }),

        "frame" => throw new PyRaise(PyErrors.AttributeError(
            TypeName, "frame: frames are not modelled in this port")),

        _ => null,
    };
}

/// <summary>A <c>Request</c>: one thing the page asked for.</summary>
internal sealed class PyRequest : PlaywrightObject
{
    private readonly IRequest _request;

    public PyRequest(Bridge bridge, IRequest request) : base(bridge) => _request = request;

    public override string TypeName => "Request";

    public override string Repr() => $"<Request {_request.Method} {_request.Url}>";

    public override PyObject? GetAttribute(string name) => name switch
    {
        "url" => new PyStr(_request.Url),
        "method" => new PyStr(_request.Method),
        "resource_type" => new PyStr(_request.ResourceType),
        "headers" => Values.FromMapping(_request.Headers),
        "post_data" => Values.FromOptional(_request.PostData),
        "failure" => Values.FromOptional(_request.Failure),

        "post_data_buffer" => _request.PostDataBuffer is { } buffer
            ? new PyBytes(buffer)
            : PyNone.Instance,

        "post_data_json" => _request.PostData is { } body
            ? Values.FromJsonText(body, "Request.post_data_json")
            : PyNone.Instance,

        "redirected_from" => _request.RedirectedFrom is { } from
            ? new PyRequest(Bridge, from)
            : PyNone.Instance,

        "redirected_to" => _request.RedirectedTo is { } to
            ? new PyRequest(Bridge, to)
            : PyNone.Instance,

        "existing_response" => _request.ExistingResponse is { } existing
            ? new PyResponse(Bridge, existing)
            : PyNone.Instance,

        "is_navigation_request" => Method("is_navigation_request", arguments =>
        {
            arguments.Done(0);
            return PyBool.Of(_request.IsNavigationRequest);
        }),

        "response" => Method("response", arguments =>
        {
            arguments.Done(0);

            var response = Bridge.Block(() => _request.ResponseAsync());
            return response is null ? PyNone.Instance : new PyResponse(Bridge, response);
        }),

        "all_headers" => Method("all_headers", arguments =>
        {
            arguments.Done(0);
            return Values.FromMapping(Bridge.Block(() => _request.AllHeadersAsync()));
        }),

        "header_value" => Method("header_value", arguments =>
        {
            var header = arguments.String(0, "name");
            arguments.Done(1);

            return Values.FromOptional(Bridge.Block(() => _request.HeaderValueAsync(header)));
        }),

        "frame" or "service_worker" or "timing" or "sizes" => throw new PyRaise(PyErrors.AttributeError(
            TypeName, $"{name}: not modelled in this port")),

        _ => null,
    };
}

/// <summary>A <c>ConsoleMessage</c>: one line the page logged.</summary>
internal sealed class PyConsoleMessage : PlaywrightObject
{
    private readonly IConsoleMessage _message;

    public PyConsoleMessage(Bridge bridge, IConsoleMessage message) : base(bridge) => _message = message;

    public override string TypeName => "ConsoleMessage";

    public override string Repr() => $"<ConsoleMessage {_message.Type}: {_message.Text}>";

    /// <inheritdoc />
    /// <remarks>
    /// Upstream's <c>__str__</c> is the message text, and scripts print these in loops, so
    /// the same shorthand is worth keeping.
    /// </remarks>
    public override string Display() => _message.Text;

    public override PyObject? GetAttribute(string name) => name switch
    {
        "type" => new PyStr(_message.Type),
        "text" => new PyStr(_message.Text),
        "timestamp" => new PyFloat(_message.Timestamp),
        "location" => new PyStr(_message.Location),

        "args" or "page" or "worker" => throw new PyRaise(PyErrors.AttributeError(
            TypeName, $"{name}: not modelled in this port; the message's text and type are")),

        _ => null,
    };
}
