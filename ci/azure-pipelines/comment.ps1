# Posts the falcon-load summary on an Azure Repos pull request for the Azure Pipelines template (falcon-load.yml). Each
# run updates the comment the previous run of the same job left, found by a hidden marker, so a pull request keeps one
# up-to-date result per job. The comment thread is closed, so it never blocks a "resolve all comments" policy.
$ErrorActionPreference = 'Stop'

if ($env:BUILD_REPOSITORY_PROVIDER -ne 'TfsGit') {
    Write-Host "Skipping the pull request comment: it is posted on Azure Repos pull requests, and this repository is on $env:BUILD_REPOSITORY_PROVIDER. Use the falcon-load GitHub Action for GitHub repositories."
    exit 0
}

$key = ("$env:COMMENT_KEY" -replace '[^A-Za-z0-9._-]', '-')
if (-not $key) { $key = 'falcon-load' }
$marker = "<!-- falcon-load:$key -->"
$runUrl = "$($env:SYSTEM_COLLECTIONURI)$([Uri]::EscapeDataString($env:SYSTEM_TEAMPROJECT))/_build/results?buildId=$($env:BUILD_BUILDID)"
$details = if ($env:UPLOADED -eq 'True') {
    "The HTML reports, JSON results and JUnit XML are in the $($env:ARTIFACT_NAME) artifact of [the pipeline run]($runUrl)."
} else {
    "See [the pipeline run]($runUrl) for details."
}

$summary = (Get-Content -Raw -Encoding utf8 $env:SUMMARY).TrimEnd()
$commit = "$env:SYSTEM_PULLREQUEST_SOURCECOMMITID"
if (-not $commit) { $commit = "$env:BUILD_SOURCEVERSION" }
$short = $commit.Substring(0, [Math]::Min(7, $commit.Length))
$content = "$marker`n$summary`n`n<sub>$details Commit $short.</sub>`n"

$base = "$($env:SYSTEM_COLLECTIONURI)$($env:SYSTEM_TEAMPROJECTID)/_apis/git/repositories/$($env:BUILD_REPOSITORY_ID)/pullRequests/$($env:SYSTEM_PULLREQUEST_PULLREQUESTID)"
$headers = @{ Authorization = "Bearer $env:SYSTEM_ACCESSTOKEN" }

function Invoke-Api([string] $method, [string] $url, $body) {
    $json = if ($null -ne $body) { $body | ConvertTo-Json -Depth 5 -Compress } else { $null }
    Invoke-RestMethod -Method $method -Uri "$url`?api-version=7.1" -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $json
}

try {
    $threads = (Invoke-Api GET "$base/threads" $null).value
    $previous = @($threads | Where-Object { -not $_.isDeleted -and $_.comments.Count -gt 0 -and "$($_.comments[0].content)".StartsWith($marker) })
    foreach ($thread in $previous) {
        try {
            Invoke-Api PATCH "$base/threads/$($thread.id)/comments/$($thread.comments[0].id)" @{ content = $content } | Out-Null
            Write-Host "Updated the falcon-load comment on pull request $env:SYSTEM_PULLREQUEST_PULLREQUESTID."
            exit 0
        } catch {
            # Only a comment's author can edit it; someone else's comment that quotes the marker is left alone.
            Write-Host "Couldn't update comment thread $($thread.id): $($_.Exception.Message)"
        }
    }

    Invoke-Api POST "$base/threads" @{ comments = @(@{ parentCommentId = 0; content = $content; commentType = 1 }); status = 'closed' } | Out-Null
    Write-Host "Posted the falcon-load comment on pull request $env:SYSTEM_PULLREQUEST_PULLREQUESTID."
} catch {
    # A missing permission shouldn't fail the build; the summary is still on the run's Extensions tab.
    Write-Host "##vso[task.logissue type=warning]falcon-load couldn't comment on the pull request: $($_.Exception.Message). Allow the build service 'Contribute to pull requests' on the repository."
}
