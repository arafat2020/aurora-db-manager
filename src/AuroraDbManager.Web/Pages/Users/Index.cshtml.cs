using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AuroraDbManager.Web.Pages.Users;

/// <summary>
/// The users. Administrators only, by the same policy as the users API: that the menu does not
/// offer this page to anyone else is a courtesy, this attribute is the rule.
/// </summary>
[Authorize(Policy = AuroraPolicies.Admin)]
public sealed class IndexModel(UserService users) : PageModel
{
    public UserListResponse Users { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Users = await users.ListAsync(new ListUsersQuery(), cancellationToken);
    }
}
