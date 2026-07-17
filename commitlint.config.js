// Conventional Commits, enforced in CI by .github/workflows/commitlint.yml. The commit types drive
// Release Please: fix -> patch, feat -> minor, `!` / BREAKING CHANGE -> major.
module.exports = {
  extends: ['@commitlint/config-conventional'],
  rules: {
    // Commit bodies here carry design rationale in prose; the 100-char wrap rule fights that. The
    // subject (header-max-length) is still capped by config-conventional.
    'body-max-line-length': [0, 'always'],
  },
};
