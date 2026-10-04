using AuroraDbManager.Api.Application.Auth;
using AuroraDbManager.Api.Application.Users;
using AuroraDbManager.Api.Errors;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuroraDbManager.Api.Controllers;

/// <summary>User management. Administrators only, every operation.</summary>
[ApiController]
[Route("api/v1/users")]
[Produces("application/json")]
[Authorize(Policy = AuroraPolicies.Admin)]
public sealed class UsersController(UserService users) : ControllerBase
{
    /// <summary>Creates a user.</summary>
    /// <remarks>
    /// The username is unique whatever its case (<c>409 USERNAME_ALREADY_EXISTS</c>) and cannot
    /// be changed afterwards. The password is hashed; neither it nor the hash is ever returned.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await users.CreateAsync(request, cancellationToken);
        return result.Status == UserStatus.Ok
            ? CreatedAtAction(nameof(Get), new { id = result.User!.Id }, result.User)
            : Failure(result.Status);
    }

    /// <summary>Lists users, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<UserListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListUsersQuery query, CancellationToken cancellationToken)
    {
        return Ok(await users.ListAsync(query, cancellationToken));
    }

    /// <summary>Returns a single user.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var user = await users.GetAsync(id, cancellationToken);
        return user is null ? Failure(UserStatus.NotFound) : Ok(user);
    }

    /// <summary>Changes a user's role, whether they may sign in, and optionally their password.</summary>
    /// <remarks>
    /// <c>role</c> and <c>enabled</c> are set to what the request says; <c>password</c> is changed
    /// only if it is given. A change takes effect at the user's next sign-in: a token already
    /// issued stays valid, with the role it was issued for, until it expires. The only enabled
    /// administrator cannot be disabled or given another role (<c>409 LAST_ADMINISTRATOR</c>).
    /// </remarks>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<UserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var result = await users.UpdateAsync(id, request, cancellationToken);
        return result.Status == UserStatus.Ok ? Ok(result.User) : Failure(result.Status);
    }

    /// <summary>Deletes a user.</summary>
    /// <remarks>The only enabled administrator cannot be deleted (<c>409 LAST_ADMINISTRATOR</c>).</remarks>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ApiErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await users.DeleteAsync(id, cancellationToken);
        return result.Status == UserStatus.Ok ? NoContent() : Failure(result.Status);
    }

    private ObjectResult Failure(UserStatus status) => status switch
    {
        UserStatus.UsernameAlreadyExists => Conflict(ApiErrorResponse.Create(
            ErrorCodes.UsernameAlreadyExists, "A user with that username already exists.")),
        UserStatus.LastAdministrator => Conflict(ApiErrorResponse.Create(
            ErrorCodes.LastAdministrator, "The only enabled administrator cannot be deleted, disabled or given another role.")),
        _ => NotFound(ApiErrorResponse.Create(ErrorCodes.UserNotFound, "User was not found."))
    };
}
