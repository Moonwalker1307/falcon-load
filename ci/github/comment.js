// Posts the falcon-load summary on the pull request for the GitHub Action (action.yml), run by actions/github-script.
// Each run updates the comment the previous run of the same job left, found by a hidden marker, so a pull request
// keeps one up-to-date result per job instead of a new comment for every push.
const fs = require('fs');

module.exports = async ({ github, context, core }) => {
  const key = (process.env.COMMENT_KEY || 'falcon-load').replace(/[^A-Za-z0-9._-]/g, '-');
  const marker = `<!-- falcon-load:${key} -->`;
  const runUrl = `${context.serverUrl}/${context.repo.owner}/${context.repo.repo}/actions/runs/${context.runId}`;
  const details = process.env.UPLOADED === 'true'
    ? `The HTML reports, JSON results and JUnit XML are in the [${process.env.ARTIFACT_NAME} artifact](${runUrl}#artifacts) of the workflow run.`
    : `See the [workflow run](${runUrl}) for details.`;

  // A comment can hold 65,536 characters; the summary is far shorter, but a run with many long names is cut to fit.
  let summary = fs.readFileSync(process.env.SUMMARY, 'utf8');
  if (summary.length > 60000) {
    summary = summary.slice(0, 60000) + '\n\n…the summary was cut to fit in a comment.\n';
  }

  const body = `${marker}\n${summary.trimEnd()}\n\n<sub>${details} Commit ${context.sha.slice(0, 7)}.</sub>\n`;
  const issue = { owner: context.repo.owner, repo: context.repo.repo, issue_number: context.issue.number };

  try {
    const comments = await github.paginate(github.rest.issues.listComments, { ...issue, per_page: 100 });
    // Only a bot's comment is updated (the default token posts as github-actions[bot]), so a person's comment that quotes
    // the marker is never overwritten.
    const previous = comments.filter(c => c.user?.type === 'Bot' && c.body?.startsWith(marker));
    for (const comment of previous) {
      try {
        await github.rest.issues.updateComment({ owner: issue.owner, repo: issue.repo, comment_id: comment.id, body });
        core.info(`Updated the falcon-load comment: ${comment.html_url}`);
        return;
      } catch (error) {
        // Another bot's comment that starts with the marker can't be edited with this token; look further or post a new one.
        if (error.status !== 403 && error.status !== 404) throw error;
      }
    }

    const { data } = await github.rest.issues.createComment({ ...issue, body });
    core.info(`Posted the falcon-load comment: ${data.html_url}`);
  } catch (error) {
    // A pull request from a fork gets a read-only token, so the comment is skipped instead of failing the job; the
    // summary is still on the job's summary page.
    if (error.status === 403 || error.status === 404) {
      core.warning(`falcon-load couldn't comment on the pull request (${error.message}). Give the job "pull-requests: write" permission; pull requests from forks get a read-only token.`);
    } else {
      throw error;
    }
  }
};
