#!/bin/bash
set -e

# ── Platform detection ──────────────────────────────────────────────
case "$(uname -s)" in
  Linux*)  RID="linux-x64"; BIN="glyphite";    NATIVE_LIB="libe_sqlite3.so";    NUGET_DIR="linux-x64" ;;
  Darwin*) RID="osx-x64";   BIN="glyphite";    NATIVE_LIB="libe_sqlite3.dylib"; NUGET_DIR="osx-x64" ;;
  MINGW*|MSYS*|CYGWIN*)
           RID="win-x64";   BIN="glyphite.exe"; NATIVE_LIB="e_sqlite3.dll";     NUGET_DIR="win-x64" ;;
  *)       echo "❌ Unknown OS: $(uname -s)"; exit 1 ;;
esac

TARGET="$HOME/.glyphite"
BACKUP_DIR="$TARGET/backup"
VERSION_FILE="$TARGET/version.txt"
PREV_VER_FILE="$TARGET/prev_version.txt"
PROJECT="$(dirname "$0")/src/Glyphite.Cli"

read_ver() { tr -d '\r' < "$1" 2>/dev/null || echo "$2"; }

mkdir -p "$BACKUP_DIR"

PREV="$TARGET/glyphite.prev"

# 1. If .prev exists, archive it to backup with its version
if [ -f "$PREV" ] && [ -f "$PREV_VER_FILE" ]; then
    PREV_VER=$(read_ver "$PREV_VER_FILE" "unknown")
    mv "$PREV" "$BACKUP_DIR/glyphite.v$PREV_VER"
    echo "📦 Archived v$PREV_VER → $BACKUP_DIR/glyphite.v$PREV_VER"
    rm -f "$PREV_VER_FILE"
elif [ -f "$PREV" ]; then
    PREV_VER=$("$PREV" -v 2>/dev/null || echo "unknown")
    mv "$PREV" "$BACKUP_DIR/glyphite.v$PREV_VER"
    echo "📦 Archived v$PREV_VER → $BACKUP_DIR/glyphite.v$PREV_VER"
fi

# 2. Current binary → .prev, save its version
CURRENT="$TARGET/$BIN"
if [ -f "$CURRENT" ]; then
    CURRENT_VER="0.0.0"
    if [ -f "$VERSION_FILE" ]; then
        CURRENT_VER=$(read_ver "$VERSION_FILE")
    else
        CURRENT_VER=$("$CURRENT" -v 2>/dev/null || echo "0.0.0")
    fi
    cp "$CURRENT" "$PREV"
    echo "$CURRENT_VER" > "$PREV_VER_FILE"
    echo "⏪ Moved current → glyphite.prev (v$CURRENT_VER)"
fi

# 3. Ensure dotnet is available
if ! command -v dotnet &>/dev/null; then
    echo "❌ dotnet not found. Install .NET SDK first: https://dotnet.microsoft.com/download"
    exit 1
fi

# 4. Build + publish new version
PYTHON=$(command -v python || command -v py || command -v python3)
if [ -z "$PYTHON" ]; then
    echo "❌ Python not found. Install Python: https://www.python.org/downloads/"
    exit 1
fi
"$PYTHON" "$(dirname "$0")/bump_version.py" build "$(dirname "$0")/version.txt"
VERSION=$(read_ver "$(dirname "$0")/version.txt" "0.0.0")
echo "🚀 Publishing v$VERSION..."
dotnet publish "$PROJECT" \
    -c Release \
    -r "$RID" \
    --self-contained \
    -p:PublishSingleFile=true \
    -p:DebugType=none \
    -p:DebugSymbols=false \
    -p:InvariantGlobalization=true 2>&1

# Copy the single-file binary
PUBLISH_DIR="$PROJECT/bin/Release/net10.0/$RID/publish"
cp "$PUBLISH_DIR/$BIN" "$TARGET/$BIN"

# Copy native libraries alongside the binary (for P/Invoke)
NUGET_SQLITE="$HOME/.nuget/packages/sqlitepclraw.lib.e_sqlite3/2.1.11/runtimes/$NUGET_DIR/native/$NATIVE_LIB"
if [ -f "$NUGET_SQLITE" ]; then
    cp "$NUGET_SQLITE" "$TARGET/$NATIVE_LIB"
fi

# ── Code sign (Windows only, self-signed dev cert) ──────────────────────────
if [ "$RID" = "win-x64" ]; then
    PFX="$TARGET/glyphite-dev.pfx"
    if [ ! -f "$PFX" ]; then
        echo "🔑 Creating self-signed code signing certificate..."
        powershell.exe -Command "
            \$cert = New-SelfSignedCertificate -Type CodeSigning -Subject 'CN=Glyphite Development' -FriendlyName 'Glyphite Development' -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3');
            \$pwd = ConvertTo-SecureString -String 'glyphite' -Force -AsPlainText;
            Export-PfxCertificate -Cert \$cert -FilePath '$PFX' -Password \$pwd;
            \$root = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser');
            \$root.Open('ReadWrite'); \$root.Add(\$cert); \$root.Close();
            \$pub = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPublisher','CurrentUser');
            \$pub.Open('ReadWrite'); \$pub.Add(\$cert); \$pub.Close();
        " 2>&1
    fi
    # Locate signtool.exe (newest Windows Kit wins)
    SIGNTOOL=$(find "/c/Program Files (x86)/Windows Kits/10/bin" -name signtool.exe -path "*/x64/*" 2>/dev/null | sort -V | tail -1)
    if [ -n "$SIGNTOOL" ]; then
        echo "🔏 Signing binary..."
        SIGNTOOL_WIN=$(cygpath -w "$SIGNTOOL")
        TARGET_WIN=$(cygpath -w "$TARGET")
        powershell.exe -Command "& { & '$SIGNTOOL_WIN' sign /a /f '$TARGET_WIN\\glyphite-dev.pfx' /p glyphite /fd SHA256 '$TARGET_WIN\\$BIN' }" 2>&1 || echo "⚠️  Signing failed (non-fatal)"
    else
        echo "⚠️  signtool.exe not found — skipping code sign"
    fi
fi

# 5. Save new version
echo "$VERSION" > "$VERSION_FILE"

echo "✅ Published v$VERSION → $TARGET/$BIN"
echo ""
echo "   Run: $TARGET/$BIN"
echo "   Rollback: cp $BACKUP_DIR/glyphite.v{VERSION} $TARGET/$BIN"
