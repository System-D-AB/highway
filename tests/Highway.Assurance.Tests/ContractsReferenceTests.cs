namespace Highway.Assurance.Tests;

using System.Reflection;
using FluentAssertions;
using Highway.Abstractions;
using Highway.Assurance.Contracts;
using Xunit;

public class ContractsReferenceTests
{
    [Fact]
    public void ContractsAssembly_ReferencesOnly_HighwayAbstractions()
    {
        var assembly = typeof(ValidateAccount).Assembly;
        var referencedAssemblies = assembly.GetReferencedAssemblies();

        var highwayRefs = referencedAssemblies
            .Where(r => r.Name != null && r.Name.StartsWith("Highway", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Name)
            .ToList();

        highwayRefs.Should().ContainSingle()
            .Which.Should().Be("Highway.Abstractions");
    }

    [Fact]
    public void AllContracts_CarryCidProperty()
    {
        var contractTypes = new[]
        {
            typeof(ValidateAccount),
            typeof(AccountResult),
            typeof(GetProfile),
            typeof(ProfileResult),
            typeof(UserSignedUp),
            typeof(PasswordResetRequested),
            typeof(AccountAudited),
            typeof(EmailDispatched),
            typeof(SendEmail)
        };

        foreach (var type in contractTypes)
        {
            var prop = type.GetProperty("Cid", BindingFlags.Public | BindingFlags.Instance);
            prop.Should().NotBeNull($"Contract {type.Name} must have a public Cid property");
            prop!.PropertyType.Should().Be(typeof(string));
        }
    }
}
