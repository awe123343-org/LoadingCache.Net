.PHONY: install-tools install-hooks format format-pre-commit format-dotnet-style check-format

PREK = uvx --from prek==0.4.14 prek

install-tools:
	pnpm install --frozen-lockfile --ignore-scripts
	dotnet tool restore

install-hooks:
	$(PREK) install

format-pre-commit:
	$(PREK) run --all-files --show-diff-on-failure

format-dotnet-style:
	$(PREK) run --hook-stage manual --all-files --show-diff-on-failure

# Run import removal before the final whitespace format. Both stages still run
# when a fixer returns nonzero because it changed files.
format:
	@status=0; $(MAKE) format-dotnet-style || status=1; \
		$(MAKE) format-pre-commit || status=1; exit $$status

check-format:
	bash pre-commit.sh
