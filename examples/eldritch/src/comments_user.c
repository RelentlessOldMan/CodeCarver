int ghost_fn(void);
int between_fn(void);
int quote_fn(void);
int spliced_fn(void);
int comments_demo(void) { return ghost_fn() * 1000 + between_fn() * 10 + quote_fn() + spliced_fn(); }
