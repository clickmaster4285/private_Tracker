.PHONY: build run dev clean test deps

# Build the server binary
build:
ifeq ($(OS),Windows_NT)
	go build -o bin/alpha-ai-server.exe ./cmd/server
else
	go build -o bin/alpha-ai-server ./cmd/server
endif

ifeq ($(OS),Windows_NT)
SERVER_BIN := ./bin/alpha-ai-server.exe
else
SERVER_BIN := ./bin/alpha-ai-server
endif

# Run the server
run: build
	$(SERVER_BIN)

# Run with hot-reload (requires air: go install github.com/air-verse/air@latest)
dev:
ifeq ($(OS),Windows_NT)
	air --build.cmd "go build -o bin/alpha-ai-server.exe ./cmd/server" --build.bin "$(SERVER_BIN)"
else
	air --build.cmd "go build -o bin/alpha-ai-server ./cmd/server" --build.bin "$(SERVER_BIN)"
endif

# Clean build artifacts
clean:
	rm -rf bin/

# Run tests
test:
	go test ./... -v -count=1

# Download dependencies
deps:
	go mod tidy
	go mod download

# Create a .env from example if it doesn't exist
setup-env:
	@if [ ! -f .env ]; then \
		cp .env.example .env; \
		echo "Created .env from .env.example — edit it with your settings"; \
	fi

# Quick start
setup: setup-env deps build
	@echo "Server built successfully. Run 'make run' to start."
