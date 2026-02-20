#!/bin/bash

# Torrentarr Build Script
# This script builds both the React frontend and .NET backend

set -e  # Exit on error

echo "========================================="
echo "   Torrentarr Build Script"
echo "========================================="
echo ""

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Check prerequisites
echo -e "${BLUE}Checking prerequisites...${NC}"

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}Error: .NET SDK not found. Please install .NET 10.0 or later.${NC}"
    exit 1
fi

if ! command -v node &> /dev/null; then
    echo -e "${RED}Error: Node.js not found. Please install Node.js 18 or later.${NC}"
    exit 1
fi

echo -e "${GREEN}✓ Prerequisites met${NC}"
echo ""

# Build frontend
echo -e "${BLUE}Building React frontend...${NC}"
cd src/Torrentarr.WebUI/ClientApp

if [ ! -d "node_modules" ]; then
    echo "Installing npm dependencies..."
    npm install
fi

echo "Building production bundle..."
npm run build

if [ $? -eq 0 ]; then
    echo -e "${GREEN}✓ Frontend build successful${NC}"
else
    echo -e "${RED}✗ Frontend build failed${NC}"
    exit 1
fi

cd ../../..
echo ""

# Build backend
echo -e "${BLUE}Building .NET backend...${NC}"
dotnet restore
dotnet build -c Release

if [ $? -eq 0 ]; then
    echo -e "${GREEN}✓ Backend build successful${NC}"
else
    echo -e "${RED}✗ Backend build failed${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}=========================================${NC}"
echo -e "${GREEN}   Build Complete!${NC}"
echo -e "${GREEN}=========================================${NC}"
echo ""
echo "Run the application:"
echo "  dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj -c Release"
echo ""
echo "Or use Docker:"
echo "  docker-compose up -d"
echo ""
