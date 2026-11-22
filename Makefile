# Slay the Spire 2 Mods - Top-level Makefile
#
# Mods are auto-detected from top-level directories containing a .csproj file.
# Adding a new mod directory requires no changes to this Makefile.

.DEFAULT_GOAL := help

CONFIGURATION ?= Release
STS2_GAME_DIR ?=

ifneq ($(strip $(STS2_GAME_DIR)),)
export STS2_GAME_DIR
DOTNET_GAME_DIR_ARG := -p:Sts2GameDir="$(STS2_GAME_DIR)"
else
DOTNET_GAME_DIR_ARG :=
endif

# Auto-detect mods from top-level directories that contain a .csproj file
MODS := $(sort $(patsubst %/,%,$(dir $(wildcard */*.csproj))))

BUILD_TARGETS         := $(addprefix build-,$(MODS))
DEPLOY_TARGETS        := $(addprefix deploy-,$(MODS))
STEAM_PREPARE_TARGETS := $(addprefix steam-prepare-,$(MODS))
STEAM_UPLOAD_TARGETS  := $(addprefix steam-upload-,$(MODS))

.PHONY: help list \
        build build-all $(BUILD_TARGETS) \
        deploy deploy-all $(DEPLOY_TARGETS) \
        steam-prepare $(STEAM_PREPARE_TARGETS) \
        steam-upload $(STEAM_UPLOAD_TARGETS)

define REQUIRE_MOD
	@if [ -z "$(MOD)" ]; then \
		echo "Error: MOD is required. Example: make $@ MOD=$(firstword $(MODS))" >&2; \
		echo "Detected mods: $(MODS)" >&2; \
		exit 1; \
	fi; \
	case " $(MODS) " in \
		*" $(MOD) "*) ;; \
		*) \
			echo "Error: Unknown mod '$(MOD)'." >&2; \
			echo "Detected mods: $(MODS)" >&2; \
			exit 1; \
			;; \
	esac
endef

help:
	@echo "Slay the Spire 2 Mods"
	@echo ""
	@echo "Detected mods:"
	@for m in $(MODS); do echo "  - $$m"; done
	@echo ""
	@echo "Targets (use either <target>-<mod> or <target> MOD=<mod>):"
	@echo "  build-<mod>           Build and test a mod without deploying"
	@echo "  deploy-<mod>          Build, test, and deploy a mod to the local STS2 mods directory"
	@echo "  steam-prepare-<mod>   Prepare/stage a Steam Workshop release for a mod"
	@echo "  steam-upload-<mod>    Prepare and upload a mod to Steam Workshop"
	@echo ""
	@echo "Generic & aggregate targets:"
	@echo "  build MOD=<mod>           Same as build-<mod>"
	@echo "  deploy MOD=<mod>          Same as deploy-<mod>"
	@echo "  steam-prepare MOD=<mod>   Same as steam-prepare-<mod>"
	@echo "  steam-upload MOD=<mod>    Same as steam-upload-<mod>"
	@echo "  build-all                 Build and test all detected mods without deploying"
	@echo "  deploy-all                Build, test, and deploy all detected mods"
	@echo "  list                      Print auto-detected mod names"
	@echo ""
	@echo "Variables:"
	@echo "  CONFIGURATION=$(CONFIGURATION)  Build configuration (Release or Debug)"
	@echo "  STS2_GAME_DIR=...      Optional override for Slay the Spire 2 install path"

list:
	@for m in $(MODS); do echo "$$m"; done

# ------------------------------------------------------------------------------
# 1. Build and test a mod without deploying
# ------------------------------------------------------------------------------
build:
	$(REQUIRE_MOD)
	@$(MAKE) --no-print-directory build-$(MOD)

build-all: $(BUILD_TARGETS)

$(BUILD_TARGETS): build-%:
	@set -eu; \
	MOD_DIR="$*"; \
	MOD_CSPROJ=""; \
	for f in "$$MOD_DIR"/*.csproj; do \
		if [ -f "$$f" ]; then \
			MOD_CSPROJ="$$f"; \
			break; \
		fi; \
	done; \
	if [ -z "$$MOD_CSPROJ" ]; then \
		echo "Error: No top-level .csproj found in $$MOD_DIR/" >&2; \
		exit 1; \
	fi; \
	echo "==> Building $$MOD_DIR ($$MOD_CSPROJ, $(CONFIGURATION), DeployToMods=false)..."; \
	dotnet build "$$MOD_CSPROJ" -c "$(CONFIGURATION)" -p:DeployToMods=false $(DOTNET_GAME_DIR_ARG); \
	FOUND_TESTS=0; \
	for proj in $$(find "$$MOD_DIR" -mindepth 2 -name '*.csproj' \
		-not -path '*/bin/*' -not -path '*/obj/*' | sort); do \
		if grep -qE '<IsTestProject>[[:space:]]*true[[:space:]]*</IsTestProject>|Microsoft\.NET\.Test\.Sdk' "$$proj"; then \
			FOUND_TESTS=1; \
			echo "==> Running dotnet test on $$proj ($(CONFIGURATION))..."; \
			dotnet test "$$proj" -c "$(CONFIGURATION)" -p:DeployToMods=false $(DOTNET_GAME_DIR_ARG); \
		elif grep -qE '<OutputType>[[:space:]]*Exe[[:space:]]*</OutputType>' "$$proj"; then \
			case "$$proj" in \
				*/tests/*|*/Tests/*|*Test*.csproj|*Tests*.csproj) \
					FOUND_TESTS=1; \
					echo "==> Running test runner $$proj ($(CONFIGURATION))..."; \
					dotnet run --project "$$proj" -c "$(CONFIGURATION)" -p:DeployToMods=false $(DOTNET_GAME_DIR_ARG); \
					;; \
			esac; \
		fi; \
	done; \
	if [ "$$FOUND_TESTS" -eq 0 ]; then \
		echo "==> No test projects found for $$MOD_DIR; skipping tests."; \
	fi

# ------------------------------------------------------------------------------
# 2. Build and test a mod and deploy it to the local mods directory
# ------------------------------------------------------------------------------
deploy:
	$(REQUIRE_MOD)
	@$(MAKE) --no-print-directory deploy-$(MOD)

deploy-all: $(DEPLOY_TARGETS)

$(DEPLOY_TARGETS): deploy-%: build-%
	@set -eu; \
	MOD_DIR="$*"; \
	MOD_CSPROJ=""; \
	for f in "$$MOD_DIR"/*.csproj; do \
		if [ -f "$$f" ]; then \
			MOD_CSPROJ="$$f"; \
			break; \
		fi; \
	done; \
	echo "==> Deploying $$MOD_DIR ($$MOD_CSPROJ, $(CONFIGURATION))..."; \
	dotnet build "$$MOD_CSPROJ" -c "$(CONFIGURATION)" -p:DeployToMods=true --no-restore $(DOTNET_GAME_DIR_ARG)

# ------------------------------------------------------------------------------
# 3. Prepare a Steam Workshop release for a mod
# ------------------------------------------------------------------------------
steam-prepare:
	$(REQUIRE_MOD)
	@$(MAKE) --no-print-directory steam-prepare-$(MOD)

$(STEAM_PREPARE_TARGETS): steam-prepare-%:
	@CONFIGURATION="$(CONFIGURATION)" sh scripts/steam-upload.sh "$*" --stage-only

# ------------------------------------------------------------------------------
# 4. Upload a mod to Steam Workshop
# ------------------------------------------------------------------------------
steam-upload:
	$(REQUIRE_MOD)
	@$(MAKE) --no-print-directory steam-upload-$(MOD)

$(STEAM_UPLOAD_TARGETS): steam-upload-%:
	@CONFIGURATION="$(CONFIGURATION)" sh scripts/steam-upload.sh "$*"
