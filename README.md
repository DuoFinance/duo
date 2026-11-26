# bond2swap

Neo N3 smart contracts for the Duo swap and bonding-curve protocol.

## Projects

- `BondingCurve/` — virtual-reserve bonding-curve contract. Sale token symbols must match `[A-Za-z0-9_-]` (max 8 chars), validated at sale creation to align with the SwapFactory pair-deployment requirements.
- `SwapFactory/`, `SwapPair/`, `SwapRouter/` — swap factory, pair, and router contracts.
- `Launchpad/` — launchpad NEP-17 token contract.
- `tests/` — contract tests, test-only token/contracts, and reentrancy test contracts.


## Build and test

The repository pins the .NET SDK in `global.json`. From the repository root, build the contracts and run all test projects with:

```sh
dotnet test bond2swap.sln
```

Neo contract projects use the `neo.compiler.csharp` local tool declared in their `dotnet-tools.json` manifests.

## License

The N3 bonding-curve source files `BondingCurve/DuoBondingCurve.cs` and
`BondingCurve/DuoBondingCurve.Helper.cs` are licensed under the unmodified
Business Source License 1.1 (`BUSL-1.1`), with the Additional Use Grant in
[`ADDITIONAL_USE_GRANT.md`](ADDITIONAL_USE_GRANT.md). The Change Date is
2029-10-31 UTC, when the Licensed Work becomes available under MIT. The
remaining original repository code is licensed under [`MIT_LICENSE`](MIT_LICENSE).
Earlier releases remain subject to the license terms distributed with those
releases. The BUSL-1.1 license is not an Open Source license during its
restriction period.
