using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace AuroraDbManager.Web.Components;

/// <summary>
/// Ties a field that the server refused to the message that says why, for someone who does not
/// see the page: the control is marked <c>aria-invalid</c> and described by its message. Nothing
/// is validated here; this only says, in the markup, what the model state already says.
/// </summary>
[HtmlTargetElement("input", Attributes = ForAttributeName)]
[HtmlTargetElement("select", Attributes = ForAttributeName)]
[HtmlTargetElement("textarea", Attributes = ForAttributeName)]
public sealed class InvalidFieldTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-for";
    private const string DescribedBy = "aria-describedby";

    // After the framework's own helpers, which write the control.
    public override int Order => 1000;

    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = null!;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var name = ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name);
        if (!ViewContext.ModelState.TryGetValue(name, out var entry) || entry.Errors.Count == 0)
        {
            return;
        }

        output.Attributes.SetAttribute("aria-invalid", "true");

        // The message, after whatever hint already describes the control.
        var described = output.Attributes[DescribedBy]?.Value?.ToString();
        var messageId = ValidationMessageIdTagHelper.IdFor(name);
        output.Attributes.SetAttribute(DescribedBy, string.IsNullOrWhiteSpace(described) ? messageId : $"{described} {messageId}");
    }
}

/// <summary>Gives a field's validation message an id, so that the field can name it as its description.</summary>
[HtmlTargetElement("span", Attributes = ForAttributeName)]
public sealed class ValidationMessageIdTagHelper : TagHelper
{
    private const string ForAttributeName = "asp-validation-for";

    public override int Order => 1000;

    [HtmlAttributeName(ForAttributeName)]
    public ModelExpression For { get; set; } = null!;

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public static string IdFor(string fullFieldName) => $"{TagBuilder.CreateSanitizedId(fullFieldName, "_")}-error";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("id"))
        {
            output.Attributes.SetAttribute("id", IdFor(ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name)));
        }
    }
}
