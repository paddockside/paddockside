// GENERATED from tokens.json by build/tokens.mjs. Do not edit; edit tokens.json and regenerate.
// Colours are CSS variables from wwwroot/tokens.css, so light and dark switch without `dark:` variants.

/** @type {import('tailwindcss').Config} */
module.exports = {
  content: ['./**/*.{razor,html,cs}', '../Paddockside.Web/**/*.{razor,html,cs}'],
  darkMode: ['selector', '[data-theme="dark"]'],
  theme: {
    "colors": {
      "transparent": "transparent",
      "current": "currentColor",
      "surface": {
        "default": "var(--color-surface-default)",
        "raised": "var(--color-surface-raised)",
        "sunken": "var(--color-surface-sunken)"
      },
      "text": {
        "primary": "var(--color-text-primary)",
        "muted": "var(--color-text-muted)",
        "inverse": "var(--color-text-inverse)"
      },
      "border": {
        "subtle": "var(--color-border-subtle)",
        "strong": "var(--color-border-strong)"
      },
      "action": {
        "primary": {
          "bg": "var(--color-action-primary-bg)",
          "bg-hover": "var(--color-action-primary-bg-hover)",
          "fg": "var(--color-action-primary-fg)"
        },
        "secondary": {
          "bg": "var(--color-action-secondary-bg)",
          "bg-hover": "var(--color-action-secondary-bg-hover)",
          "fg": "var(--color-action-secondary-fg)"
        }
      },
      "focus": {
        "ring": "var(--color-focus-ring)"
      },
      "status": {
        "success": {
          "fg": "var(--color-status-success-fg)",
          "bg": "var(--color-status-success-bg)"
        },
        "warning": {
          "fg": "var(--color-status-warning-fg)",
          "bg": "var(--color-status-warning-bg)"
        },
        "danger": {
          "fg": "var(--color-status-danger-fg)",
          "bg": "var(--color-status-danger-bg)"
        },
        "info": {
          "fg": "var(--color-status-info-fg)",
          "bg": "var(--color-status-info-bg)"
        }
      },
      "scope": {
        "internal": {
          "fg": "var(--color-scope-internal-fg)",
          "bg": "var(--color-scope-internal-bg)"
        },
        "owners": {
          "fg": "var(--color-scope-owners-fg)",
          "bg": "var(--color-scope-owners-bg)"
        },
        "owners-at-the-time": {
          "fg": "var(--color-scope-owners-at-the-time-fg)",
          "bg": "var(--color-scope-owners-at-the-time-bg)"
        },
        "named-parties": {
          "fg": "var(--color-scope-named-parties-fg)",
          "bg": "var(--color-scope-named-parties-bg)"
        },
        "trainer": {
          "fg": "var(--color-scope-trainer-fg)",
          "bg": "var(--color-scope-trainer-bg)"
        }
      },
      "kind": {
        "fact": {
          "fg": "var(--color-kind-fact-fg)",
          "bg": "var(--color-kind-fact-bg)"
        },
        "message": {
          "fg": "var(--color-kind-message-fg)",
          "bg": "var(--color-kind-message-bg)"
        },
        "media": {
          "fg": "var(--color-kind-media-fg)",
          "bg": "var(--color-kind-media-bg)"
        },
        "note": {
          "fg": "var(--color-kind-note-fg)",
          "bg": "var(--color-kind-note-bg)"
        }
      }
    },
    "fontFamily": {
      "ui": "var(--font-ui)"
    },
    "fontSize": {
      "meta": [
        "var(--text-size-meta)",
        {
          "lineHeight": "var(--leading-meta)"
        }
      ],
      "small": [
        "var(--text-size-small)",
        {
          "lineHeight": "var(--leading-small)"
        }
      ],
      "body": [
        "var(--text-size-body)",
        {
          "lineHeight": "var(--leading-body)"
        }
      ],
      "body-owner": [
        "var(--text-size-body-owner)",
        {
          "lineHeight": "var(--leading-body-owner)"
        }
      ],
      "title": [
        "var(--text-size-title)",
        {
          "lineHeight": "var(--leading-title)"
        }
      ],
      "heading": [
        "var(--text-size-heading)",
        {
          "lineHeight": "var(--leading-heading)"
        }
      ],
      "display": [
        "var(--text-size-display)",
        {
          "lineHeight": "var(--leading-display)"
        }
      ]
    },
    "fontWeight": {
      "regular": "var(--weight-regular)",
      "medium": "var(--weight-medium)",
      "strong": "var(--weight-strong)",
      "heading": "var(--weight-heading)"
    },
    "spacing": {
      "0": "0",
      "1": "var(--space-1)",
      "2": "var(--space-2)",
      "3": "var(--space-3)",
      "4": "var(--space-4)",
      "6": "var(--space-6)",
      "8": "var(--space-8)",
      "12": "var(--space-12)",
      "16": "var(--space-16)",
      "px": "1px"
    },
    "borderRadius": {
      "none": "0",
      "control": "var(--radius-control)",
      "card": "var(--radius-card)",
      "pill": "var(--radius-pill)"
    },
    "boxShadow": {
      "none": "none",
      "raised": "var(--elevation-raised)",
      "overlay": "var(--elevation-overlay)"
    },
    "transitionDuration": {
      "fast": "var(--motion-fast)",
      "base": "var(--motion-base)"
    },
    "extend": {
      "minHeight": {
        "control": "var(--control-height)"
      },
      "height": {
        "control": "var(--control-height)"
      },
      "padding": {
        "card": "var(--card-padding)"
      },
      "gap": {
        "stream-item": "var(--stream-item-gap)"
      }
    }
  },
};
