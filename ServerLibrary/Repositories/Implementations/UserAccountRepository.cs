using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BaseLibrary.DTOs;
using BaseLibrary.Entities;
using BaseLibrary.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ServerLibrary.Data;
using ServerLibrary.Helpers;
using ServerLibrary.Repositories.Contracts;
using Constants = ServerLibrary.Helpers.Constants;

namespace ServerLibrary.Repositories.Implementations;

public class UserAccountRepository(IOptions<JwtSection> config, AppDbContext appDbContext) : IUserAccount {
	public async Task<GeneralResponse> CreateAsync(Register? user) {
		if (user is null) return new GeneralResponse(false, "Model is Empty");

		var checkUser = await FindUserByEmail(user.Email!);
		if (checkUser != null) return new GeneralResponse(false, "User registered already");

		var applicationUser = await AddToDatabase(new ApplicationUser() {
			FullName = user.Fullname,
			Email    = user.Email,
			Password = BCrypt.Net.BCrypt.HashPassword(user.Password)
		});

		var checkAdminRole = await appDbContext.SystemRoles.FirstOrDefaultAsync(r => r.Name!.Equals(Constants.Admin));
		if (checkAdminRole is null) {
			var createAdminRole = await AddToDatabase(new SystemRole { Name = Constants.Admin });
			await AddToDatabase(new UserRole { RoleId = createAdminRole.Id, UserId = applicationUser.Id });
			return new GeneralResponse(true, "Account created!");
		}

		var checkUserRole = await appDbContext.SystemRoles.FirstOrDefaultAsync(r => r.Name!.Equals(Constants.User));
		if (checkUserRole is null) {
			var response = await AddToDatabase(new SystemRole { Name = Constants.Admin });
			await AddToDatabase(new UserRole { RoleId = response.Id, UserId = applicationUser.Id });
		} else {
			await AddToDatabase(new UserRole { RoleId = checkUserRole.Id, UserId = applicationUser.Id });
		}

		return new GeneralResponse(true, "Account created!");
	}

	public async Task<LoginResponse> SignInAsync(Login? user) {
		if (user is null) return new LoginResponse(false, "Model is empty");

		var applicationUser = await FindUserByEmail(user.Email!);
		if (applicationUser is null) return new LoginResponse(false, "User not found");

		if (!BCrypt.Net.BCrypt.Verify(user.Password, applicationUser.Password))
			return new LoginResponse(false, "Email/Password not valid");

		var getUserRole = await FindUserRole(applicationUser.Id);
		if (getUserRole is null) return new LoginResponse(false, "User role not found");

		var getRoleName = await FindRoleName(getUserRole.RoleId);
		if (getRoleName is null) return new LoginResponse(false, "User role not found");

		var jwtToken     = GenerateToken(applicationUser, getRoleName.Name!);
		var refreshToken = GenerateRefreshToken();

		var findUser = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(u => u.UserId == applicationUser.Id);
		if (findUser is not null) {
			findUser.Token = refreshToken;
			await appDbContext.SaveChangesAsync();
		} else {
			await AddToDatabase(new RefreshTokenInfo() { Token = refreshToken, UserId = applicationUser.Id });
		}
		return new LoginResponse(true, "Login successfully", jwtToken, refreshToken);
	}

	public async Task<LoginResponse> RefreshTokenAsync(RefreshToken? token) {
		if (token is null) return new LoginResponse(false, "Model is empty");

		var findToken = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(t => t.Token!.Equals(token.Token));
		if (findToken is null) return new LoginResponse(false, "Refresh token is required");

		var user = await appDbContext.ApplicationUsers.FirstOrDefaultAsync(u => u.Id == findToken.UserId);
		if (user is null)
			return new LoginResponse(false, "Refresh token could not be generated because user not was not found");

		var userRole           = await FindUserRole(user.Id);
		var roleName           = await FindRoleName(userRole!.UserId);
		var jwtToken           = GenerateToken(user, roleName?.Name!);
		var refreshToken       = GenerateRefreshToken();
		var updateRefreshToken = await appDbContext.RefreshTokenInfos.FirstOrDefaultAsync(u => u.Id == user.Id);
		if (updateRefreshToken is null)
			return new LoginResponse(false, "Refresh token could not be generated because use has not signed in");

		updateRefreshToken.Token = refreshToken;
		await appDbContext.SaveChangesAsync();
		return new LoginResponse(true, "Token refreshed successfully", jwtToken, refreshToken);
	}

	private async Task<UserRole?> FindUserRole(int userId) =>
		await appDbContext.UserRoles.FirstOrDefaultAsync(r => r!.UserId == userId);

	private async Task<SystemRole?> FindRoleName(int roleId) =>
		await appDbContext.SystemRoles.FirstOrDefaultAsync(r => r.Id == roleId);

	private static string GenerateRefreshToken() =>
		Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

	private string GenerateToken(ApplicationUser user, string? role) {
		var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config.Value.Key!));
		var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
		var userClaims = new[] {
			new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
			new Claim(ClaimTypes.Name,           user.FullName!),
			new Claim(ClaimTypes.Email,          user.Email!),
			new Claim(ClaimTypes.Role,           role!),
		};
		var token = new JwtSecurityToken(issuer: config.Value.Issuer,
		audience: config.Value.Audience,
		claims: userClaims,
		expires: DateTime.Now.AddDays(1),
		signingCredentials: credentials);
		return new JwtSecurityTokenHandler().WriteToken(token);
	}

	private async Task<ApplicationUser?> FindUserByEmail(string email) {
		return await appDbContext.ApplicationUsers.FirstOrDefaultAsync(u =>
			u.Email!.ToLower().Equals(email.ToLower()));
	}

	private async Task<T> AddToDatabase<T>(T model) {
		var result = appDbContext.Add(model!);
		await appDbContext.SaveChangesAsync();
		return (T)result.Entity;
	}
}
