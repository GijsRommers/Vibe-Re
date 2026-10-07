# Usage: make install            -> installs `vibe-re` and `vibe-re-server` into ~/.local/bin
#        make install PREFIX=/usr/local   (may need sudo)
#
# `vibe-re` starts the selected local server automatically when needed.

PREFIX    ?= $(HOME)/.local
BINDIR    ?= $(PREFIX)/bin
CONFIG    ?= Release
RID       ?= $(shell dotnet --info | awk '/RID:/ { print $$2; exit }')
OUT       := artifacts/$(RID)
BINARIES  := vibe-re vibe-re-server

# Self-contained single files, so the installed binaries don't depend on a local .NET runtime.
PUBLISH   := dotnet publish -c $(CONFIG) -r $(RID) --self-contained \
             -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $(OUT)

.PHONY: all build test publish install uninstall server stop clean

all: build

build:
	dotnet build vibe-re.sln -c $(CONFIG)

test:
	dotnet test vibe-re.sln -c $(CONFIG)

publish:
	$(PUBLISH) VibeRe.Cli
	$(PUBLISH) VibeRe.Server

# Stops a running server before replacing the installed binary.
install: publish stop
	install -d "$(BINDIR)"
	for bin in $(BINARIES); do install -m 755 "$(OUT)/$$bin" "$(BINDIR)/$$bin"; done
	@echo "Installed $(BINARIES) into $(BINDIR)"
	@case ":$$PATH:" in *":$(BINDIR):"*) ;; *) echo "Note: $(BINDIR) is not on your PATH." ;; esac

uninstall: stop
	for bin in $(BINARIES); do rm -f "$(BINDIR)/$$bin"; done

# Runs the server in the foreground from source, for development.
server:
	dotnet run --project VibeRe.Server

stop:
	@pkill -x vibe-re-server || true

clean:
	dotnet clean vibe-re.sln
	rm -rf artifacts
