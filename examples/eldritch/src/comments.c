/* Lexer traps. The ghost below is inside a // comment continued by a backslash: it does not exist.
   The real ghost_fn is in ghost.c. */
// the old ghost lived here \
int ghost_fn(void) { return 666; }

static const char *open_c = "/*";
int between_fn(void) { return 12; }   /* a naive comment stripper eats this */
static const char *close_c = "*/";

static const char q1 = '"';
int quote_fn(void) { return 13; }
static const char q2 = '"';

/* this comment ends at the spliced star-slash: *\
/ int spliced_fn(void) { return 14; }
