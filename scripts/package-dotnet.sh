#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(dirname -- "$script_dir")"

package_project() {
  local project_name="$1"
  local package_name="$2"
  local project_dir="$project_root/src/$project_name"
  local output_package="$project_dir/publish/$package_name"

  echo "Packaging .NET Lambda project: src/$project_name"
  dotnet lambda package \
    --project-location "$project_dir" \
    --configuration Release \
    --function-architecture arm64 \
    --output-package "$output_package"
}

package_project "OlxWatcher.ListingsApi" "listings-api.zip"
package_project "OlxWatcher.ListingsWatcher" "listings-watcher.zip"
