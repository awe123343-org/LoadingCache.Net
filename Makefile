.PHONY: install-tools install-hooks format format-pre-commit format-dotnet-style check-format check-format-changes format-success

PREK = uvx prek@latest
DOTNET_FORMAT_VERBOSITY ?= normal
ifeq ($(CI),true)
DOTNET_FORMAT_VERBOSITY := diagnostic
endif

install-tools:
	pnpm install --frozen-lockfile --ignore-scripts
	dotnet tool restore

install-hooks:
	$(PREK) install

format-pre-commit:
	@echo "📋 Running pre-commit hooks..."
	@uvx prek run \
		--hook-stage manual \
		--all-files

format-dotnet-style:
	@echo "🎨 Running dotnet format style..."
	@echo "ℹ️ dotnet SDK: $$(dotnet --version)"
	@echo "ℹ️ dotnet format verbosity: $(DOTNET_FORMAT_VERBOSITY)"
	dotnet restore LoadingCache.slnx
	dotnet format style LoadingCache.slnx --no-restore --diagnostics IDE0005 --severity info --verbosity $(DOTNET_FORMAT_VERBOSITY)

format: format-pre-commit
	@echo "✨ Formatting complete!"

check-format:
	bash pre-commit.sh

check-format-changes:
	@echo ""
	@echo "❌ Formatting failed or tracked files differ from HEAD!"
	@echo ""
	@echo "📝 Showing colored diff:"
	@echo ""
	@git --no-pager diff HEAD --color=always
	@echo ""
	@echo "💡 To apply these changes, run:"
	@echo "  make format"
	@echo ""
	@echo "🚫 Exiting with code 1"

format-success:
	@echo ""
	@echo "🎉 All files are already properly formatted!"
