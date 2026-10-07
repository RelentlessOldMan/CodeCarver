/* Book of Horrors #3 and #4: C with the C removed, and exceptions made of loops. */
#define BEGIN {
#define END }
#define INTEGER int
#define ASSIGN =
#define WHILE(x) while (x)
#define DO
#define TRY do {
#define CATCH } while (0); if (error)
#define THROW(x) do { error = (x); break; } while (0)

INTEGER pascal_sum(INTEGER n)
BEGIN
    INTEGER s ASSIGN 0;
    WHILE(n > 0) DO
    BEGIN
        s ASSIGN s + n--;
    END
    return s;
END

static int error;
int try_fn(int v)
{
    TRY
        if (v > 2) THROW(v);
    CATCH
        return error * 2;
    return 0;
}

/* A body opened by a macro and closed by a brace: the text is unbalanced. */
#define OPEN {
int half_open(int v) OPEN return v + 1; }
int after_open(void) { return 9; }
