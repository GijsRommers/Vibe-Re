# Usage: make install            -> installs `vibe-re` and `vibe-re-server` into ~/.local/bin
#        make install PREFIX=/usr/local   (may need sudo)
#
# `vibe-re` starts the selected local server automatically when needed.

PREFIX    ?= $(HOME)/.local
BINDIR    ?= $(PREFIX)/bin
LIBDIR    ?= $(PREFIX)/lib/vibe-re
CONFIG    ?= Release
RID       ?= $(shell dotnet --info | awk '/RID:/ { print $$2; exit }')
OUT       := artifacts/stage/$(RID)
BINARIES  := vibe-re vibe-re-server

# Preserve complete, isolated publish trees, including settings and native libraries.
PUBLISH   := dotnet publish -c $(CONFIG) -r $(RID) --self-contained \
             -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None

.PHONY: all build test publish install uninstall server stop clean

all: build

build:
	dotnet build vibe-re.Desktop.slnf -c $(CONFIG)

test:
	dotnet test vibe-re.Desktop.slnf -c $(CONFIG)

publish:
	$(PUBLISH) VibeRe.Cli -o "$(OUT)/cli"
	$(PUBLISH) VibeRe.Server -o "$(OUT)/server"

# Stops a running server before replacing the installed files.
install: publish stop
	install -d "$(BINDIR)"
	install -d "$(LIBDIR)"
	cp -R "$(OUT)/cli" "$(OUT)/server" "$(LIBDIR)/"
	ln -sfn "$(abspath $(LIBDIR))/cli/vibe-re" "$(BINDIR)/vibe-re"
	ln -sfn "$(abspath $(LIBDIR))/server/vibe-re-server" "$(BINDIR)/vibe-re-server"
	@echo "Installed $(BINARIES) into $(BINDIR)"
	@case ":$$PATH:" in *":$(BINDIR):"*) ;; *) echo "Note: $(BINDIR) is not on your PATH." ;; esac

uninstall: stop
	for bin in $(BINARIES); do rm -f "$(BINDIR)/$$bin"; done
	rm -rf "$(LIBDIR)/cli" "$(LIBDIR)/server"

# Runs the server in the foreground from source, for development.
server:
	dotnet run --project VibeRe.Server

stop:
	@pkill -x vibe-re-server || true

clean:
	dotnet clean vibe-re.Desktop.slnf
	rm -rf artifacts
