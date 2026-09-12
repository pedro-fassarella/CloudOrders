# Validation

## Results

- `dotnet restore CloudOrders.sln`: passed.
- `dotnet build CloudOrders.sln --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet test tests/CloudOrders.UnitTests/CloudOrders.UnitTests.csproj --no-restore --no-build`: 6 passed.
- `dotnet test tests/CloudOrders.IntegrationTests/CloudOrders.IntegrationTests.csproj --no-restore --no-build`: 5 passed against PostgreSQL 17 started by Testcontainers.
- `dotnet ef migrations has-pending-model-changes`: no model changes since the existing migration.
- `dotnet list CloudOrders.sln package --vulnerable --include-transitive`: no vulnerable packages reported.

## Remaining warning

The installed global `dotnet-ef` tool is `10.0.5`, while the EF Core runtime packages are `10.0.11`. The schema verification completed successfully. Updating a global developer tool is outside this change and was not performed.
