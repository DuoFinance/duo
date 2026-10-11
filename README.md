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

See [LICENSE](LICENSE) for the licensing terms. The virtual-reserve bonding-curve algorithm is subject to a temporary commercial-use restriction from 2026-10-31 through 2029-10-30, then becomes MIT-licensed. Other repository contents are MIT-licensed. Earlier releases remain subject to their accompanying license terms.
