# bond2swap

Neo N3 smart contracts for the Duo swap and bonding-curve protocol.

## Projects

- `BondingCurve/` — virtual-reserve bonding-curve contract.
- `SwapFactory/`, `SwapPair/`, `SwapRouter/` — swap factory, pair, and router contracts.
- `Launchpad/` — launchpad NEP-17 token contract.
- `tests/` — contract tests, test-only token/contracts, and reentrancy test contracts.

Contract source files retain their existing names; the project and test folders are organized separately from contract source folders.

## Build and test

The repository pins the .NET SDK in `global.json`. From the repository root, build the contracts and run all test projects with:

```sh
dotnet test bond2swap.sln
```

Neo contract projects use the `neo.compiler.csharp` local tool declared in their `dotnet-tools.json` manifests.

## License

See [LICENSE](LICENSE) for the MIT terms and the additional three-year restriction that applies specifically to the virtual-reserve bonding-curve algorithm.
