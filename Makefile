# Usage: make install            -> installs `vibe-re-server` into ~/.local/bin
#        make install PREFIX=/usr/local   (may need sudo)

PREFIX    ?= $(HOME)/.local
BINDIR    ?= $(PREFIX)/bin
LIBDIR    ?= $(PREFIX)/lib/vibe-re
CONFIG    ?= Release
RID       ?= $(shell dotnet --info | awk '/RID:/ { print $$2; exit }')
OUT       := artifacts/stage/$(RID)

# Preserve a complete, isolated publish tree, including settings and native libraries.
PUBLISH   := dotnet publish -c $(CONFIG) -r $(RID) --self-contained \
             -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None

.PHONY: all build test publish install uninstall server stop clean

all: build

build:
	dotnet build vibe-re.sln -c $(CONFIG)

test:
	dotnet test vibe-re.sln -c $(CONFIG)

publish:
	$(PUBLISH) VibeRe.Server -o "$(OUT)/server"

# Stops a running server before replacing the installed files.
install: publish stop
	install -d "$(BINDIR)"
	install -d "$(LIBDIR)"
	cp -R "$(OUT)/server" "$(LIBDIR)/"
	ln -sfn "$(abspath $(LIBDIR))/server/vibe-re-server" "$(BINDIR)/vibe-re-server"
	@echo "Installed vibe-re-server into $(BINDIR)"
	@case ":$$PATH:" in *":$(BINDIR):"*) ;; *) echo "Note: $(BINDIR) is not on your PATH." ;; esac

uninstall: stop
	rm -f "$(BINDIR)/vibe-re-server"
	rm -rf "$(LIBDIR)/server"

# Runs the server in the foreground from source, for development.
server:
	dotnet run --project VibeRe.Server

stop:
	@pkill -x vibe-re-server || true

clean:
	dotnet clean vibe-re.sln
	rm -rf artifacts
