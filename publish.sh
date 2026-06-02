#!/bin/bash
set -e

TARGET="$HOME/.glyphite"
BACKUP_DIR="$TARGET/backup"
VERSION_FILE="$TARGET/version.txt"
PREV_VER_FILE="$TARGET/prev_version.txt"
PROJECT="$(dirname "$0")/src/Glyphite.Cli"

read_ver() { tr -d '\r' < "$1" 2>/dev/null || echo "$2"; }

mkdir -p "$BACKUP_DIR"

# 1. If .prev exists, archive it to backup with its version
if [ -f "$TARGET/glyphite.prev" ] && [ -f "$PREV_VER_FILE" ]; then
    PREV_VER=$(read_ver "$PREV_VER_FILE" "unknown")
    mv "$TARGET/glyphite.prev" "$BACKUP_DIR/glyphite.v$PREV_VER"
    echo "📦 Archived v$PREV_VER → $BACKUP_DIR/glyphite.v$PREV_VER"
    rm -f "$PREV_VER_FILE"
elif [ -f "$TARGET/glyphite.prev" ]; then
    # No version file — try to get version from binary
    PREV_VER=$("$TARGET/glyphite.prev" -v 2>/dev/null || echo "unknown")
    mv "$TARGET/glyphite.prev" "$BACKUP_DIR/glyphite.v$PREV_VER"
    echo "📦 Archived v$PREV_VER → $BACKUP_DIR/glyphite.v$PREV_VER"
fi

# 2. Current binary → .prev, save its version
if [ -f "$TARGET/glyphite" ]; then
    CURRENT_VER="0.0.0"
    if [ -f "$VERSION_FILE" ]; then
        CURRENT_VER=$(read_ver "$VERSION_FILE")
    else
        CURRENT_VER=$("$TARGET/glyphite" -v 2>/dev/null || echo "0.0.0")
    fi
    cp "$TARGET/glyphite" "$TARGET/glyphite.prev"
    echo "$CURRENT_VER" > "$PREV_VER_FILE"
    echo "⏪ Moved current → glyphite.prev (v$CURRENT_VER)"
fi

# 3. Ensure dotnet is available
if ! command -v dotnet &>/dev/null; then
    echo "❌ dotnet not found. Install .NET SDK first: https://dotnet.microsoft.com/download"
    exit 1
fi

# 4. Build + publish new version
python3 "$(dirname "$0")/bump_version.py" build "$(dirname "$0")/version.txt"
VERSION=$(read_ver "$(dirname "$0")/version.txt" "0.0.0")
echo "🚀 Publishing v$VERSION..."
dotnet publish "$PROJECT" \
    -c Release \
    -r linux-x64 \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:InvariantGlobalization=true 2>&1

# Copy the single-file binary
cp "$PROJECT/bin/Release/net10.0/linux-x64/publish/glyphite" "$TARGET/glyphite"

# Copy native libraries alongside the binary (for P/Invoke)
NUGET_SQLITE="$HOME/.nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.11/runtimes/linux-x64/native/libe_sqlite3.so"
if [ -f "$NUGET_SQLITE" ]; then
    cp "$NUGET_SQLITE" "$TARGET/libe_sqlite3.so"
fi

# 5. Save new version
echo "$VERSION" > "$VERSION_FILE"

echo "✅ Published v$VERSION → $TARGET/glyphite"
echo ""
echo "   Run: glyphite"
echo "   Rollback: cp $BACKUP_DIR/glyphite.v{VERSION} $TARGET/glyphite"
