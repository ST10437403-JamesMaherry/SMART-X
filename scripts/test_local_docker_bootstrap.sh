#!/bin/sh
# Fast, dependency-light verification of the zero-config Docker key bootstrap.
# Runs with a fake dotnet command; the real .NET API is covered by CI separately.
set -eu

repo_root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
temp_dir="$(mktemp -d)"
trap 'rm -rf "$temp_dir"' EXIT HUP INT TERM

mkdir -p "$temp_dir/keys" "$temp_dir/bin"
# Adapt only the absolute Docker volume path for an isolated local smoke test.
sed "s|/data/keys|$temp_dir/keys|g" \
  "$repo_root/docker/local-api-entrypoint.sh" > "$temp_dir/start.sh"

cat > "$temp_dir/bin/dotnet" <<'EOF'
#!/bin/sh
printf '%s' "${Attachments__EncryptionKeyBase64:-}" > "$SMARTX_TEST_CAPTURE"
EOF
chmod 755 "$temp_dir/bin/dotnet"
export PATH="$temp_dir/bin:$PATH"
export SMARTX_TEST_CAPTURE="$temp_dir/captured-key"

# Fresh startup must generate a valid 256-bit key and write it privately.
env -u Attachments__EncryptionKeyBase64 sh "$temp_dir/start.sh"
key_file="$temp_dir/keys/attachment-key.base64"
test -s "$key_file"
cmp -s "$key_file" "$SMARTX_TEST_CAPTURE" || {
  # The key file has a newline from openssl, while the exported value does not.
  [ "$(cat "$key_file")" = "$(cat "$SMARTX_TEST_CAPTURE")" ] || exit 1
}
bytes="$(openssl base64 -d -A < "$SMARTX_TEST_CAPTURE" | wc -c | tr -d ' ')"
[ "$bytes" = 32 ]
cp "$SMARTX_TEST_CAPTURE" "$temp_dir/original"

# Restart must reuse the existing key, not invalidate encrypted attachments.
env -u Attachments__EncryptionKeyBase64 sh "$temp_dir/start.sh"
cmp -s "$temp_dir/original" "$SMARTX_TEST_CAPTURE"

# Optional override must not overwrite the key in the persistent volume.
Attachments__EncryptionKeyBase64="temporary-test-override" sh "$temp_dir/start.sh"
[ "$(cat "$SMARTX_TEST_CAPTURE")" = "temporary-test-override" ]
[ "$(cat "$key_file")" = "$(cat "$temp_dir/original")" ]

# Corrupted (empty) stored key must fail rather than silently rotate.
: > "$key_file"
if env -u Attachments__EncryptionKeyBase64 sh "$temp_dir/start.sh" > "$temp_dir/error.log" 2>&1; then
  echo "Empty key was unexpectedly accepted" >&2
  exit 1
fi

echo "Local Docker key bootstrap checks passed."
